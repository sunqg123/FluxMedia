using FFmpeg.AutoGen;
using FluxMedia.Core.Configuration;
using FluxMedia.Core.Logging;
using FluxMedia.FFmpeg.Native;
using FluxMedia.Playback;
using FluxMedia.Playback.Internal;
using System.Threading.Channels;

namespace FluxMedia.FFmpeg.Pipeline;

/// <summary>
/// 将播放数据包封装为录制文件。
/// </summary>
internal sealed unsafe class FFmpegPacketRemuxRecorder : IAsyncDisposable
{
    /// <summary>
    /// 写入队列容量。
    /// </summary>
    private const int WriteQueueCapacity = 512;

    /// <summary>
    /// 保护组件共享状态的同步锁。
    /// </summary>
    private readonly object _syncRoot = new();

    /// <summary>
    /// 保护录制写入状态的锁。
    /// </summary>
    private readonly object _writeGate = new();

    /// <summary>
    /// 等待封装写入的录制数据包队列。
    /// </summary>
    private readonly Channel<FFmpegPacketLease> _writeQueue;

    /// <summary>
    /// 配置。
    /// </summary>
    private PlaybackRecordingOptions _options;

    /// <summary>
    /// 实例标识。
    /// </summary>
    private readonly Guid _instanceId;

    /// <summary>
    /// 视频流。
    /// </summary>
    private readonly FFmpegStreamDescriptor? _videoStream;

    /// <summary>
    /// 音频流。
    /// </summary>
    private readonly FFmpegStreamDescriptor? _audioStream;

    /// <summary>
    /// 输出流。
    /// </summary>
    private readonly Dictionary<int, nint> _outputStreams = new();

    /// <summary>
    /// 各源流的时间基。
    /// </summary>
    private readonly Dictionary<int, AVRational> _inputTimeBases = new();

    /// <summary>
    /// 各输出流的时间戳偏移量。
    /// </summary>
    private readonly Dictionary<int, long> _timestampOffsets = new();

    /// <summary>
    /// 各输出流最近写入的解码时间戳。
    /// </summary>
    private readonly Dictionary<int, long> _lastOutputDts = new();

    /// <summary>
    /// FFmpeg 的原生容器上下文指针。
    /// </summary>
    private AVFormatContext* _formatContext;

    /// <summary>
    /// 最近一次录制结果。
    /// </summary>
    private PlaybackRecordingResult _result;

    /// <summary>
    /// 连续发生的输出写入失败次数。
    /// </summary>
    private int _consecutiveWriteFailures;

    /// <summary>
    /// 最近写入失败日志时刻。
    /// </summary>
    private DateTimeOffset? _lastWriteFailureLogAt;

    /// <summary>
    /// 当前输入分段的时间起点（微秒）。
    /// </summary>
    private long? _segmentOriginUs;

    /// <summary>
    /// 当前分段映射到输出的起点（微秒）。
    /// </summary>
    private long _segmentOutputBaseUs;

    /// <summary>
    /// 已写入数据的最晚结束时间（微秒）。
    /// </summary>
    private long _maxOutputEndUs;

    /// <summary>
    /// 已写入数据包数量。
    /// </summary>
    private long _writtenPacketCount;

    /// <summary>
    /// 写入器任务。
    /// </summary>
    private Task? _writerTask;

    /// <summary>
    /// 是否因时间线跳变暂停写入。
    /// </summary>
    private bool _writeSuspended;

    /// <summary>
    /// 是否仍接收新的录制数据包。
    /// </summary>
    private bool _acceptingWrites;

    /// <summary>
    /// 是否已启动。
    /// </summary>
    private bool _started;

    /// <summary>
    /// 是否已释放资源。
    /// </summary>
    private bool _disposed;

    /// <summary>
    /// 初始化 FFmpeg 数据包重新封装录制器所需的状态和资源。
    /// </summary>
    public FFmpegPacketRemuxRecorder(PlaybackRecordingOptions options, Guid instanceId, FFmpegStreamDescriptor? videoStream, FFmpegStreamDescriptor? audioStream)
    {
        _options = options;
        _instanceId = instanceId;
        _videoStream = videoStream;
        _audioStream = audioStream;
        _writeQueue = Channel.CreateBounded<FFmpegPacketLease>(new BoundedChannelOptions(WriteQueueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });
        FFmpegNativeRuntime.EnsureInitialized();
        _result = new PlaybackRecordingResult
        {
            State = PlaybackRecordingState.Idle,
            OutputPath = options.OutputPath,
        };
    }

    /// <summary>
    /// 读取实际录制结果，不访问输出文件。
    /// </summary>
    public PlaybackRecordingResult Result
    {
        get
        {
            lock (_syncRoot)
            {
                return _result with { WrittenPackets = Interlocked.Read(ref _writtenPacketCount) };
            }
        }
    }

    /// <summary>
    /// 启动录制输出和后台封装任务。
    /// </summary>
    public void Start()
    {
        lock (_syncRoot)
        {
            ThrowIfDisposed();
            if (_started) return;
            try
            {
                PrepareOutputPath();
                OpenOutput();
            }
            catch (Exception exception)
            {
                try { CloseOutput(); }
                catch (Exception cleanup)
                {
                    throw new AggregateException("录制打开失败，关闭输出时也发生错误。", exception, cleanup);
                }
                throw;
            }
            _started = true;
            _acceptingWrites = true;
            _result = _result with
            {
                State = PlaybackRecordingState.Recording,
                LastError = null
            };
            _writerTask = Task.Run(WriteLoop);
            FluxMediaLog.Write(LogLevel.Information, nameof(FFmpegPacketRemuxRecorder), _instanceId, $"Event=PacketRemuxRecordingStart OutputPath={_options.OutputPath} HasVideo={_videoStream is not null} HasAudio={_audioStream is not null}");
        }
    }

    /// <summary>
    /// 尝试将数据包加入待写入队列。
    /// </summary>
    public void TryWrite(AVPacket* sourcePacket)
    {
        if (sourcePacket != null && (sourcePacket->flags & ffmpeg.AV_PKT_FLAG_CORRUPT) != 0)
        {
            if (sourcePacket->stream_index == _videoStream?.StreamIndex)
            {
                SuspendForDiscontinuity();
                ResumeAfterDiscontinuity();
            }
            return;
        }
        FFmpegPacketLease? packetLease = null;
        lock (_syncRoot)
        {
            if (!_started || !_acceptingWrites || _disposed || _writeSuspended || sourcePacket == null || !_outputStreams.ContainsKey(sourcePacket->stream_index)) return;
            try
            {
                packetLease = FFmpegPacketLease.Clone(sourcePacket, 0);
            }
            catch
            {
                return;
            }
        }
        if (!_writeQueue.Writer.TryWrite(packetLease))
        {
            packetLease.Dispose();
        }
    }

    /// <summary>
    /// 按顺序封装队列中的录制数据包。
    /// </summary>
    private void WriteLoop()
    {
        try
        {
            while (_writeQueue.Reader.WaitToReadAsync().AsTask().GetAwaiter().GetResult())
            {
                while (_writeQueue.Reader.TryRead(out var packetLease))
                {
                    using (packetLease)
                    {
                        lock (_writeGate)
                        {
                            WritePacket(packetLease.Packet);
                        }
                    }
                }
            }
        }
        finally
        {
            while (_writeQueue.Reader.TryRead(out var pending))
            {
                pending.Dispose();
            }
        }
    }

    /// <summary>
    /// 转换时间戳并将数据包写入输出容器。
    /// </summary>
    private void WritePacket(AVPacket* packet)
    {
        if (!_started || _writeSuspended || packet == null || !_outputStreams.TryGetValue(packet->stream_index, out var outputStreamPtr))
        {
            return;
        }

        try
        {
            var outputStream = (AVStream*)outputStreamPtr;
            var inputTimeBase = _inputTimeBases[packet->stream_index];
            if (!TryEstablishSegmentOrigin(packet, inputTimeBase))
            {
                return;
            }

            var sourceDtsUs = PacketTimestampUs(packet->dts, inputTimeBase);
            var sourcePtsUs = PacketTimestampUs(packet->pts, inputTimeBase);
            if ((sourceDtsUs is not null && sourceDtsUs < _segmentOriginUs) || (sourcePtsUs is not null && sourcePtsUs < _segmentOriginUs))
            {
                return;
            }

            packet->dts = MapSegmentTimestamp(sourceDtsUs, outputStream->time_base);
            packet->pts = MapSegmentTimestamp(sourcePtsUs, outputStream->time_base);
            packet->duration = ffmpeg.av_rescale_q(packet->duration, inputTimeBase, outputStream->time_base);

            NormalizeOutputTimestamps(packet, outputStream->index);
            packet->stream_index = outputStream->index;
            packet->pos = -1;
            // 写入会清空数据包，提前保留时间线所需的值。
            var outputTimestamp = packet->dts != ffmpeg.AV_NOPTS_VALUE ? packet->dts : packet->pts;
            var outputDuration = packet->duration;
            var write = ffmpeg.av_interleaved_write_frame(_formatContext, packet);
            FFmpegErrorHelper.ThrowIfError(write, "av_interleaved_write_frame");
            UpdateOutputEnd(outputTimestamp, outputDuration, outputStream->time_base);
            _consecutiveWriteFailures = 0;
            Interlocked.Increment(ref _writtenPacketCount);
        }
        catch (Exception ex)
        {
            _consecutiveWriteFailures++;
            lock (_syncRoot)
            {
                _result = _result with
                {
                    LastError = ex.Message
                };
            }
            var now = DateTimeOffset.UtcNow;
            if (_lastWriteFailureLogAt is null || now - _lastWriteFailureLogAt >= TimeSpan.FromSeconds(2))
            {
                _lastWriteFailureLogAt = now;
                FluxMediaLog.Write(LogLevel.Warning, nameof(FFmpegPacketRemuxRecorder), _instanceId, $"Event=PacketRemuxWriteFailed OutputPath={_options.OutputPath} ConsecutiveFailures={_consecutiveWriteFailures} Error={ex.Message}", ex);
            }

            if (_consecutiveWriteFailures >= 8)
            {
                AbortAfterWriteFailures(ex);
            }
        }
    }

    /// <summary>
    /// 暂停写入并等待新的时间分段。
    /// </summary>
    public void SuspendForDiscontinuity()
    {
        lock (_syncRoot)
        {
            if (!_started || _disposed) return;
            _writeSuspended = true;
        }
        lock (_writeGate)
        {
            while (_writeQueue.Reader.TryRead(out var pending))
            {
                pending.Dispose();
            }
            FluxMediaLog.Write(LogLevel.Information, nameof(FFmpegPacketRemuxRecorder), _instanceId, $"Event=PacketRemuxDiscontinuitySuspended OutputBaseUs={_maxOutputEndUs}");
        }
    }

    /// <summary>
    /// 在新的时间分段恢复录制。
    /// </summary>
    public void ResumeAfterDiscontinuity()
    {
        lock (_writeGate)
        {
            _segmentOutputBaseUs = _maxOutputEndUs;
            _segmentOriginUs = null;
        }

        lock (_syncRoot)
        {
            if (!_started || _disposed) return;
            _writeSuspended = false;
            FluxMediaLog.Write(
                LogLevel.Information, nameof(FFmpegPacketRemuxRecorder), _instanceId, $"Event=PacketRemuxDiscontinuityResumed OutputBaseUs={_segmentOutputBaseUs} WaitingForVideoKeyFrame={_videoStream is not null}");
        }
    }

    /// <summary>
    /// 确定当前录制分段的输入时间起点。
    /// </summary>
    private bool TryEstablishSegmentOrigin(AVPacket* packet, AVRational inputTimeBase)
    {
        if (_segmentOriginUs is not null) return true;
        if (_videoStream is not null)
        {
            if (packet->stream_index != _videoStream.StreamIndex || (packet->flags & ffmpeg.AV_PKT_FLAG_KEY) == 0) return false;
        }
        var timestamp = packet->dts != ffmpeg.AV_NOPTS_VALUE ? packet->dts : packet->pts;
        if (timestamp == ffmpeg.AV_NOPTS_VALUE) return false;
        _segmentOriginUs = ffmpeg.av_rescale_q(timestamp, inputTimeBase, new AVRational { num = 1, den = ffmpeg.AV_TIME_BASE });
        FluxMediaLog.Write(
            LogLevel.Information, nameof(FFmpegPacketRemuxRecorder), _instanceId, $"Event=PacketRemuxTimelineStarted StreamIndex={packet->stream_index} OriginUs={_segmentOriginUs.Value} OutputBaseUs={_segmentOutputBaseUs} KeyFrame={(packet->flags & ffmpeg.AV_PKT_FLAG_KEY) != 0}");
        return true;
    }

    /// <summary>
    /// 将分段时间映射到连续的输出时间线。
    /// </summary>
    private long MapSegmentTimestamp(long? sourceTimestampUs, AVRational outputTimeBase)
    {
        if (sourceTimestampUs is null)
        {
            return ffmpeg.AV_NOPTS_VALUE;
        }

        var mappedUs = _segmentOutputBaseUs + sourceTimestampUs.Value - _segmentOriginUs!.Value;
        return ffmpeg.av_rescale_q(Math.Max(0, mappedUs), new AVRational { num = 1, den = ffmpeg.AV_TIME_BASE }, outputTimeBase);
    }

    /// <summary>
    /// 将数据包时间戳换算为微秒。
    /// </summary>
    private static long? PacketTimestampUs(long timestamp, AVRational inputTimeBase)
    {
        return timestamp == ffmpeg.AV_NOPTS_VALUE ? null : ffmpeg.av_rescale_q(timestamp, inputTimeBase, new AVRational { num = 1, den = ffmpeg.AV_TIME_BASE });
    }

    /// <summary>
    /// 更新已输出内容的结束时间。
    /// </summary>
    private void UpdateOutputEnd(long timestamp, long duration, AVRational outputTimeBase)
    {
        if (timestamp == ffmpeg.AV_NOPTS_VALUE) return;
        var end = timestamp + Math.Max(0, duration);
        var endUs = ffmpeg.av_rescale_q(end, outputTimeBase, new AVRational { num = 1, den = ffmpeg.AV_TIME_BASE });
        _maxOutputEndUs = Math.Max(_maxOutputEndUs, endUs);
    }

    /// <summary>
    /// 取消待执行操作并停止播放管线。
    /// </summary>
    public ValueTask StopAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Task? writerTask;
        lock (_syncRoot)
        {
            if (_disposed) return default;
            _acceptingWrites = false;
            _writeQueue.Writer.TryComplete();
            writerTask = _writerTask;
        }
        if (writerTask is null || writerTask.IsCompleted)
        {
            writerTask?.GetAwaiter().GetResult();
            FinishAfterDrain();
            return default;
        }
        return new ValueTask(FFmpegPacketRemuxRecorderAwaiter.AwaitAndRunAsync(writerTask, FinishAfterDrain));
    }

    /// <summary>
    /// 排空待写入数据包后结束录制。
    /// </summary>
    private void FinishAfterDrain()
    {
        lock (_writeGate)
        {
            lock (_syncRoot)
            {
                if (!_started || _result.State != PlaybackRecordingState.Recording) return;
            }

            Finish();
        }
    }

    /// <summary>
    /// 释放当前对象持有的资源。
    /// </summary>
    public ValueTask DisposeAsync()
    {
        ValueTask stop;
        try { stop = StopAsync(); }
        catch
        {
            ReleaseResources();
            throw;
        }
        if (!stop.IsCompletedSuccessfully)
        {
            return new ValueTask(FFmpegPacketRemuxRecorderAwaiter.AwaitAndFinallyAsync(stop.AsTask(), ReleaseResources));
        }

        ReleaseResources();
        return default;
    }

    /// <summary>
    /// 写入任务退出后关闭文件，即使任务失败也不遗留原生资源。
    /// </summary>
    private void ReleaseResources()
    {
        lock (_writeGate)
        {
            lock (_syncRoot) { if (_disposed) return; }
            try { CloseOutput(); }
            catch (Exception exception)
            {
                lock (_syncRoot)
                {
                    _result = _result with
                    {
                        State = PlaybackRecordingState.Failed,
                        OutputFileFinalized = false,
                        LastError = exception.Message
                    };
                }
                throw;
            }
            finally
            {
                lock (_syncRoot)
                {
                    _disposed = true;
                    _acceptingWrites = false;
                    _started = false;
                    if (_result.State == PlaybackRecordingState.Recording)
                    {
                        _result = _result with
                        {
                            State = PlaybackRecordingState.Failed,
                            OutputFileFinalized = false,
                            LastError = _result.LastError ?? "录制写入任务异常退出，文件未完成封装。"
                        };
                    }
                }
            }
        }
    }

    /// <summary>
    /// 创建输出上下文并写入文件头。
    /// </summary>
    private void OpenOutput()
    {
        AVFormatContext* output = null;
        FFmpegErrorHelper.ThrowIfError(ffmpeg.avformat_alloc_output_context2(&output, null, null, _options.OutputPath), "avformat_alloc_output_context2");
        _formatContext = output;
        if (_formatContext == null)
        {
            throw new InvalidOperationException("Failed to allocate recording output context.");
        }

        if (_videoStream is not null)
        {
            AddStream(_videoStream);
        }

        if (_audioStream is not null)
        {
            AddStream(_audioStream);
        }

        if (_outputStreams.Count == 0)
        {
            throw new InvalidOperationException("No audio or video streams are available for packet remux recording.");
        }

        if ((_formatContext->oformat->flags & ffmpeg.AVFMT_NOFILE) == 0)
        {
            FFmpegErrorHelper.ThrowIfError(ffmpeg.avio_open(&_formatContext->pb, _options.OutputPath, ffmpeg.AVIO_FLAG_WRITE), "avio_open");
        }

        FFmpegErrorHelper.ThrowIfError(ffmpeg.avformat_write_header(_formatContext, null), "avformat_write_header");
    }

    /// <summary>
    /// 复制源流参数并添加输出流。
    /// </summary>
    private void AddStream(FFmpegStreamDescriptor inputStream)
    {
        var outputStream = ffmpeg.avformat_new_stream(_formatContext, null);
        if (outputStream == null)
        {
            throw new InvalidOperationException("Failed to create recording output stream.");
        }

        FFmpegErrorHelper.ThrowIfError(ffmpeg.avcodec_parameters_copy(outputStream->codecpar, inputStream.CodecParameters), "avcodec_parameters_copy");
        outputStream->codecpar->codec_tag = 0;
        outputStream->time_base = inputStream.TimeBase;
        _outputStreams[inputStream.StreamIndex] = (nint)outputStream;
        _inputTimeBases[inputStream.StreamIndex] = inputStream.TimeBase;
    }

    /// <summary>
    /// 修正输出时间戳，保持解码时间递增。
    /// </summary>
    private void NormalizeOutputTimestamps(AVPacket* packet, int outputStreamIndex)
    {
        if (packet->dts == ffmpeg.AV_NOPTS_VALUE) return;
        var offset = _timestampOffsets.TryGetValue(outputStreamIndex, out var currentOffset) ? currentOffset : 0;
        var adjustedDts = packet->dts + offset;
        if (_lastOutputDts.TryGetValue(outputStreamIndex, out var lastDts) && adjustedDts <= lastDts)
        {
            offset += lastDts + 1 - adjustedDts;
            _timestampOffsets[outputStreamIndex] = offset;
            adjustedDts = packet->dts + offset;
        }

        packet->dts = adjustedDts;
        if (packet->pts != ffmpeg.AV_NOPTS_VALUE)
        {
            packet->pts += offset;
            if (packet->pts < packet->dts)
            {
                packet->pts = packet->dts;
            }
        }

        _lastOutputDts[outputStreamIndex] = packet->dts;
    }

    /// <summary>
    /// 连续写入失败达到阈值时终止录制。
    /// </summary>
    private void AbortAfterWriteFailures(Exception exception)
    {
        lock (_syncRoot)
        {
            _result = _result with
            {
                State = PlaybackRecordingState.Failed,
                LastError = $"Recording stopped after {_consecutiveWriteFailures} consecutive packet write failures: {exception.Message}",
                OutputFileFinalized = false,
            };
            _started = false;
            _acceptingWrites = false;
            _writeQueue.Writer.TryComplete();
        }
        FluxMediaLog.Write(
            LogLevel.Error, nameof(FFmpegPacketRemuxRecorder), _instanceId, $"Event=PacketRemuxRecordingAborted OutputPath={_options.OutputPath} ConsecutiveFailures={_consecutiveWriteFailures} Error={exception.Message}", exception);
        CloseOutput();
    }

    /// <summary>
    /// 完成录制封装并更新最终状态。
    /// </summary>
    private void Finish()
    {
        Exception? failure = null;
        var trailerWritten = false;
        var outputClosed = false;
        try
        {
            FFmpegErrorHelper.ThrowIfError(ffmpeg.av_write_trailer(_formatContext), "av_write_trailer");
            trailerWritten = true;
        }
        catch (Exception exception) { failure = exception; }

        try
        {
            CloseOutput();
            outputClosed = true;
        }
        catch (Exception exception)
        {
            failure = failure is null ? exception : new AggregateException("录制封装和关闭均失败。", failure, exception);
        }

        var writtenPackets = Interlocked.Read(ref _writtenPacketCount);
        if (failure is null && writtenPackets == 0)
            failure = new InvalidOperationException("未写入有效媒体数据包，录制文件不可播放。");

        lock (_syncRoot)
        {
            _started = false;
            _acceptingWrites = false;
            _result = _result with
            {
                State = failure is null ? PlaybackRecordingState.Completed : PlaybackRecordingState.Failed,
                OutputFileFinalized = trailerWritten && outputClosed,
                LastError = failure?.Message,
            };
        }

        if (failure is not null)
            FluxMediaLog.Write(LogLevel.Error, nameof(FFmpegPacketRemuxRecorder), _instanceId, $"Event=PacketRemuxRecordingFailed OutputPath={_options.OutputPath} Error={failure.Message}", failure);
    }

    /// <summary>
    /// 关闭输出文件及封装上下文。
    /// </summary>
    private void CloseOutput()
    {
        var context = _formatContext;
        if (context == null) return;
        _formatContext = null;
        var closeResult = 0;
        try
        {
            if (context->pb != null) closeResult = ffmpeg.avio_closep(&context->pb);
        }
        finally
        {
            // IO 关闭失败也必须归还格式上下文。
            ffmpeg.avformat_free_context(context);
        }
        FFmpegErrorHelper.ThrowIfError(closeResult, "avio_closep");
    }

    /// <summary>
    /// 检查输出路径及覆盖策略。
    /// </summary>
    private void PrepareOutputPath()
    {
        if (string.IsNullOrWhiteSpace(_options.OutputPath))
        {
            throw new ArgumentException("Recording output path cannot be empty.", nameof(_options.OutputPath));
        }

        var fullPath = Path.GetFullPath(_options.OutputPath);
        var directory = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new ArgumentException("Unable to resolve recording output directory.", nameof(_options.OutputPath));
        }

        if (!Directory.Exists(directory))
        {
            if (!_options.CreateOutputDirectory)
            {
                throw new DirectoryNotFoundException($"Recording output directory does not exist: {directory}");
            }

            Directory.CreateDirectory(directory);
        }

        if (File.Exists(fullPath))
        {
            if (!_options.OverwriteExisting)
            {
                throw new IOException($"Recording output file already exists: {fullPath}");
            }

            File.Delete(fullPath);
        }

        _options = _options with
        {
            OutputPath = fullPath
        };
    }

    /// <summary>
    /// 对象已释放时阻止继续操作。
    /// </summary>
    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(FFmpegPacketRemuxRecorder));
        }
    }
}
