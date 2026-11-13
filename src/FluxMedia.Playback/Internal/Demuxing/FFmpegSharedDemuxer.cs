using LogConverge.Sharp;
using FFmpeg.AutoGen;
using FluxMedia.Core.Models;
using FluxMedia.FFmpeg.Native;
using FluxMedia.Playback;
using FluxMedia.Playback.Internal;
using System.Collections.Concurrent;
using System.Threading.Channels;

namespace FluxMedia.FFmpeg.Pipeline;

/// <summary>
/// 共享一次媒体读取，将数据包分发到音视频队列。
/// </summary>
internal sealed unsafe partial class FFmpegSharedDemuxer : IDisposable
{
    /// <summary>
    /// 源。
    /// </summary>
    private readonly MediaSource _source;

    /// <summary>
    /// 配置。
    /// </summary>
    private readonly PlaybackOptions _options;

    /// <summary>
    /// 实例标识。
    /// </summary>
    private readonly Guid _instanceId;

    /// <summary>
    /// 开始位置。
    /// </summary>
    private readonly TimeSpan _startPosition;

    /// <summary>
    /// 是否通过丢弃旧包降低网络延迟。
    /// </summary>
    private bool _dropPacketsForLowLatency;

    /// <summary>
    /// 串行执行原生读取和跳转的锁。
    /// </summary>
    private readonly ManualResetEventSlim _readGate;

    /// <summary>
    /// 等待视频解码消费的数据包队列。
    /// </summary>
    private readonly FFmpegPacketQueue _videoPackets;

    /// <summary>
    /// 等待音频解码消费的数据包队列。
    /// </summary>
    private readonly FFmpegPacketQueue _audioPackets;

    /// <summary>
    /// 保护单次资源释放的锁。
    /// </summary>
    private readonly object _disposeSync = new();

    /// <summary>
    /// 等待原生读取线程处理的跳转请求队列。
    /// </summary>
    private readonly ConcurrentQueue<DemuxSeekRequest> _seekRequests = new();

    /// <summary>
    /// 取消整个播放生命周期的信号源。
    /// </summary>
    private CancellationTokenSource? _lifetimeCts;

    /// <summary>
    /// 通知媒体和流信息已就绪的完成信号。
    /// </summary>
    private TaskCompletionSource<bool>? _ready;

    /// <summary>
    /// 解复用任务。
    /// </summary>
    private Task? _demuxTask;

    /// <summary>
    /// 是否已请求释放解复用器。
    /// </summary>
    private volatile bool _disposed;

    /// <summary>
    /// 是否已进入单次资源清理。
    /// </summary>
    private bool _resourcesDisposed;

    /// <summary>
    /// 初始化 FFmpeg 共享解复用器所需的状态和资源。
    /// </summary>
    public FFmpegSharedDemuxer(MediaSource source, PlaybackOptions options, Guid instanceId, TimeSpan startPosition, int videoQueueCapacity = 4, int audioQueueCapacity = 24, bool deferPacketReading = false)
    {
        _source = source;
        _options = options;
        _instanceId = instanceId;
        _startPosition = startPosition;
        _readGate = new ManualResetEventSlim(!deferPacketReading);
        _dropPacketsForLowLatency = source.Kind == MediaSourceKind.NetworkStream && options.Network.LowLatencyMode;
        if (_dropPacketsForLowLatency)
        {
            videoQueueCapacity = Math.Max(videoQueueCapacity, 60);
            audioQueueCapacity = Math.Max(audioQueueCapacity, 120);
        }
        else if (source.Kind == MediaSourceKind.NetworkStream)
        {
            videoQueueCapacity = Math.Max(videoQueueCapacity, 180);
            audioQueueCapacity = Math.Max(audioQueueCapacity, 384);
        }

        _videoPackets = new FFmpegPacketQueue(videoQueueCapacity, requireKeyFrameAfterOverflow: _dropPacketsForLowLatency);
        _audioPackets = new FFmpegPacketQueue(audioQueueCapacity);
    }

    /// <summary>
    /// 视频流。
    /// </summary>
    public FFmpegStreamDescriptor? VideoStream
    {
        get; private set;
    }

    /// <summary>
    /// 音频流。
    /// </summary>
    public FFmpegStreamDescriptor? AudioStream
    {
        get; private set;
    }

    /// <summary>
    /// 媒体信息就绪后确定的基础时序模式。
    /// </summary>
    public PlaybackTimingMode TimingMode { get; private set; } = PlaybackTimingMode.Auto;

    /// <summary>
    /// 视频数据包。
    /// </summary>
    public ChannelReader<FFmpegPacketLease> VideoPackets => _videoPackets.Reader;

    /// <summary>
    /// 音频数据包。
    /// </summary>
    public ChannelReader<FFmpegPacketLease> AudioPackets => _audioPackets.Reader;

    /// <summary>
    /// 当前处理到的媒体时间。
    /// </summary>
    public TimeSpan CurrentTime => VideoStream is not null ? _videoPackets.CurrentTime : _audioPackets.CurrentTime;

    /// <summary>
    /// 缓冲时长。
    /// </summary>
    public TimeSpan BufferedDuration => VideoStream is not null ? _videoPackets.BufferedDuration : _audioPackets.BufferedDuration;

    /// <summary>
    /// 视频缓冲时长。
    /// </summary>
    public TimeSpan VideoBufferedDuration => _videoPackets.BufferedDuration;

    /// <summary>
    /// 音频缓冲时长。
    /// </summary>
    public TimeSpan AudioBufferedDuration => _audioPackets.BufferedDuration;

    /// <summary>
    /// 视频包数量。
    /// </summary>
    public int VideoPacketCount => _videoPackets.Count;

    /// <summary>
    /// 媒体信息。
    /// </summary>
    public MediaInfo? MediaInfo
    {
        get; private set;
    }

    /// <summary>
    /// 当前原生读取位置对应的数据包代次。
    /// </summary>
    private long _packetGeneration;

    /// <summary>
    /// 供解码器判断手中数据包是否已经过期。
    /// </summary>
    public long PacketGeneration => Interlocked.Read(ref _packetGeneration);

    /// <summary>
    /// 原生读取任务是否已失败，用于判断网络恢复。
    /// </summary>
    public bool HasFailed => Volatile.Read(ref _failure) is not null;

    /// <summary>
    /// 在通知下游失败前保存读取异常，供重连判断。
    /// </summary>
    private Exception? _failure;

    /// <summary>
    /// 启动后台处理任务。
    /// </summary>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (_demuxTask is not null)
        {
            return _demuxTask;
        }

        _lifetimeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _ready = new TaskCompletionSource<bool>();
        _demuxTask = Task.Run(() => DemuxLoop(_lifetimeCts.Token), CancellationToken.None);
        return _demuxTask;
    }

    /// <summary>
    /// 等待媒体打开和流信息准备完成。
    /// </summary>
    public Task WaitUntilReadyAsync(CancellationToken cancellationToken)
    {
        var ready = _ready ?? throw new InvalidOperationException("The demuxer has not been started.");
        return DemuxerTaskWaiter.WaitAsync(ready.Task, cancellationToken);
    }

    /// <summary>
    /// 允许解复用线程开始读取数据包。
    /// </summary>
    public void ActivatePacketReading()
    {
        _readGate.Set();
    }

    /// <summary>
    /// 跳转到指定媒体时间。
    /// </summary>
    public ValueTask SeekAsync(TimeSpan target, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        EnsureSeekable();
        if (_demuxTask is null)
        {
            throw new InvalidOperationException("The demuxer has not been started.");
        }

        if (_demuxTask.IsCompleted)
        {
            var exception = _demuxTask.Exception?.GetBaseException() ?? new InvalidOperationException("The demuxer is no longer running.");
            return new ValueTask(Task.FromException(exception));
        }

        var request = new DemuxSeekRequest(target);
        _seekRequests.Enqueue(request);
        var registration = cancellationToken.CanBeCanceled ? cancellationToken.Register(static state =>
            {
                var seek = (DemuxSeekRequest)state!;
                seek.TryCancel();
            }, request) : default;
        if (cancellationToken.CanBeCanceled)
        {
            _ = request.Task.ContinueWith(static (_, state) => ((CancellationTokenRegistration)state!).Dispose(), registration, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }

        return new ValueTask(request.Task);
    }

    /// <summary>
    /// 停止当前处理并保留必要的清理状态。
    /// </summary>
    public void Stop()
    {
        try
        {
            _lifetimeCts?.Cancel();
        }
        catch (Exception exception)
        {
            Log.Error(nameof(FFmpegSharedDemuxer),
                $"SessionId={_instanceId} Event=DemuxerCancelFailed", exception);
        }
        CompletePendingSeekRequests(new OperationCanceledException("The demuxer has been stopped."));
        _videoPackets.Complete();
        _audioPackets.Complete();
    }

    /// <summary>
    /// 释放当前对象持有的资源。
    /// </summary>
    public void Dispose()
    {
        Task? demuxTask;
        lock (_disposeSync)
        {
            if (_disposed)
                return;
            _disposed = true;
            Stop();
            demuxTask = _demuxTask;
        }

        if (demuxTask is null || demuxTask.IsCompleted)
        {
            DisposeResources();
            return;
        }

        // 原生读取可能尚未退出，等线程结束后再释放队列和流参数。
        _ = demuxTask.ContinueWith(static (_, state) =>
        {
            var demuxer = (FFmpegSharedDemuxer)state!;
            try
            {
                demuxer.DisposeResources();
            }
            catch (Exception exception)
            {
                Log.Error(nameof(FFmpegSharedDemuxer),
                    $"SessionId={demuxer._instanceId} Event=DeferredDemuxerCleanupFailed", exception);
            }
        }, this, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    /// <summary>
    /// 释放包队列、流描述和录制资源。
    /// </summary>
    private void DisposeResources()
    {
        lock (_disposeSync)
        {
            if (_resourcesDisposed)
                return;
            _resourcesDisposed = true;
        }

        var errors = new List<Exception>();
        Release(_videoPackets.Dispose);
        Release(_audioPackets.Dispose);
        Release(_readGate.Dispose);
        FFmpegPacketRemuxRecorder? recording;
        lock (_recordingSync)
        {
            recording = _recording;
            _recording = null;
        }
        Release(() => { lock (_recordingSync) ClearRecordingPreroll(); });
        Release(() => recording?.SuspendForDiscontinuity());
        Release(() => VideoStream?.Dispose());
        Release(() => AudioStream?.Dispose());
        Release(() => _lifetimeCts?.Dispose());
        if (errors.Count > 0)
            throw new AggregateException("解复用资源释放失败。", errors);

        // 每项资源均尝试清理，最后统一报告异常。
        void Release(Action release)
        {
            try
            {
                release();
            }
            catch (Exception exception) { errors.Add(exception); }
        }
    }

    /// <summary>
    /// 打开媒体并循环分发音视频数据包。
    /// </summary>
    private void DemuxLoop(CancellationToken cancellationToken)
    {
        FFmpegNativeRuntime.EnsureInitialized();

        AVFormatContext* formatContext = null;
        AVDictionary* dictionary = null;
        PlaybackInterruptContext? interrupt = null;
        AVPacket* packet = null;

        try
        {
            interrupt = PlaybackFFmpegOptions.CreateInterruptContext(_source.Kind == MediaSourceKind.NetworkStream ? _options.Network.OpenTimeout : _options.OpenTimeout, cancellationToken);
            dictionary = PlaybackFFmpegOptions.CreateOpenDictionary(_source, _options);
            formatContext = ffmpeg.avformat_alloc_context();
            if (formatContext == null)
            {
                throw new InvalidOperationException("FFmpeg format context allocation failed.");
            }

            formatContext->interrupt_callback = interrupt.Callback;
            var openResult = ffmpeg.avformat_open_input(&formatContext, _source.Uri, null, &dictionary);
            FFmpegErrorHelper.ThrowIfError(openResult, "avformat_open_input");

            interrupt.ResetDeadline();
            var streamInfoResult = ffmpeg.avformat_find_stream_info(formatContext, null);
            FFmpegErrorHelper.ThrowIfError(streamInfoResult, "avformat_find_stream_info");

            VideoStream = FindStream(formatContext, AVMediaType.AVMEDIA_TYPE_VIDEO);
            AudioStream = FindStream(formatContext, AVMediaType.AVMEDIA_TYPE_AUDIO);
            MediaInfo = FFmpegMediaInfoReader.BuildMediaInfo(formatContext);
            var timingMode = PlaybackTimingPolicy.Resolve(_source, _options, MediaInfo.Duration);
            TimingMode = timingMode;
            _dropPacketsForLowLatency = PlaybackTimingPolicy.IsLiveNetworkSource(_source, MediaInfo.Duration) && _options.Network.LowLatencyMode && timingMode == PlaybackTimingMode.RealtimeLowLatency;
            if (VideoStream is not null)
            {
                _videoPackets.ConfigureTiming(VideoStream.TimeBase, EstimateVideoPacketDuration(VideoStream), VideoStream.StartTime);
            }

            if (AudioStream is not null)
            {
                _audioPackets.ConfigureTiming(AudioStream.TimeBase, EstimateAudioPacketDuration(formatContext, AudioStream), AudioStream.StartTime);
            }

            _ready?.TrySetResult(true);
            _readGate.Wait(cancellationToken);

            if (_startPosition > TimeSpan.Zero)
            {
                interrupt.ResetDeadline();
                SeekFormatContext(formatContext, _startPosition);
            }

            if (_source.Kind == MediaSourceKind.NetworkStream)
            {
                interrupt.SetTimeout(_options.Network.ReadTimeout);
            }

            packet = ffmpeg.av_packet_alloc();
            if (packet == null)
            {
                throw new InvalidOperationException("FFmpeg packet allocation failed.");
            }
            Func<bool> seekPending = () => !_seekRequests.IsEmpty;
            while (!_disposed && !cancellationToken.IsCancellationRequested)
            {
                while (_seekRequests.TryDequeue(out var seek))
                {
                    if (seek.IsCompleted)
                        continue;
                    try
                    {
                        interrupt.ResetDeadline();
                        SeekFormatContext(formatContext, seek.Target);
                        _videoPackets.Clear();
                        _audioPackets.Clear();
                        ffmpeg.avformat_flush(formatContext);
                        seek.TrySetResult();
                    }
                    catch (Exception ex)
                    {
                        seek.TrySetException(ex);
                    }
                }

                int read;
                interrupt.BeginRead(seekPending);
                try
                {
                    do
                    {
                        read = ffmpeg.av_read_frame(formatContext, packet);
                        if (read != ffmpeg.AVERROR(ffmpeg.EAGAIN) || interrupt.ShouldInterrupt())
                            break;
                        cancellationToken.WaitHandle.WaitOne(2);
                    } while (true);
                }
                finally { interrupt.EndRead(); }
                if (read < 0)
                {
                    if (interrupt.ReadInterrupted && !cancellationToken.IsCancellationRequested && !_disposed)
                    {
                        // 主动跳转不触发断流恢复，清除本次中断留下的 IO 错误。
                        if (formatContext->pb != null)
                        {
                            formatContext->pb->error = 0;
                            formatContext->pb->eof_reached = 0;
                        }
                        ffmpeg.av_packet_unref(packet);
                        continue;
                    }
                    var timedOut = interrupt.TimedOut && !cancellationToken.IsCancellationRequested && !_disposed;
                    if (cancellationToken.IsCancellationRequested || _disposed)
                    {
                        break;
                    }
                    if (timedOut)
                    {
                        throw new TimeoutException($"FFmpeg shared demux read timed out: {_source.Uri}");
                    }

                    if (read == ffmpeg.AVERROR_EOF && !PlaybackTimingPolicy.IsLiveNetworkSource(_source, MediaInfo.Duration))
                    {
                        break;
                    }
                    // 直播 EOF 和非 EOF 读取错误均不能伪装为自然结束。
                    throw new IOException($"Media read interrupted ({read}): {FFmpegErrorHelper.GetErrorMessageOrNull(read)}");
                }

                try
                {
                    if (VideoStream is not null && packet->stream_index == VideoStream.StreamIndex)
                    {
                        RoutePacketWithBackpressure(_videoPackets, FFmpegPacketLease.Clone(packet, PacketGeneration), cancellationToken);
                    }
                    else if (AudioStream is not null && packet->stream_index == AudioStream.StreamIndex)
                    {
                        RoutePacketWithBackpressure(_audioPackets, FFmpegPacketLease.Clone(packet, PacketGeneration), cancellationToken);
                    }
                }
                finally
                {
                    ffmpeg.av_packet_unref(packet);
                }
            }
        }
        catch (Exception exception) when (cancellationToken.IsCancellationRequested || _disposed)
        {
            _ready?.TrySetCanceled();
            throw new OperationCanceledException("媒体读取已取消。", exception, cancellationToken);
        }
        catch (Exception ex)
        {
            Interlocked.CompareExchange(ref _failure, ex, null);
            _ready?.TrySetException(ex);
            CompletePendingSeekRequests(ex);
            _videoPackets.Complete(ex);
            _audioPackets.Complete(ex);

            throw;
        }
        finally
        {
            CompletePendingSeekRequests(new OperationCanceledException("The demuxer has exited."));
            _videoPackets.Complete();
            _audioPackets.Complete();
            if (packet != null)
            {
                ffmpeg.av_packet_free(&packet);
            }
            if (dictionary != null)
            {
                ffmpeg.av_dict_free(&dictionary);
            }
            if (formatContext != null)
            {
                ffmpeg.avformat_close_input(&formatContext);
            }
            interrupt?.Dispose();
        }
    }

    /// <summary>
    /// 在任何原生定位之前校验点播权限。
    /// </summary>
    private void EnsureSeekable()
    {
        if (MediaInfo is null || !PlaybackTimingPolicy.CanSeek(_source, MediaInfo.Duration))
        {
            throw new NotSupportedException("直播或未知总时长的媒体不允许调整播放进度。");
        }
    }

    /// <summary>
    /// 调用 FFmpeg 将媒体读取位置移到目标时间。
    /// </summary>
    private void SeekFormatContext(AVFormatContext* formatContext, TimeSpan target)
    {
        EnsureSeekable();
        var seekTarget = (long)(target.TotalSeconds * ffmpeg.AV_TIME_BASE);
        var result = ffmpeg.av_seek_frame(formatContext, -1, seekTarget, ffmpeg.AVSEEK_FLAG_BACKWARD);

        FFmpegErrorHelper.ThrowIfError(result, "av_seek_frame");
        Interlocked.Increment(ref _packetGeneration);
    }

    /// <summary>
    /// 按延迟策略丢包或等待队列腾出空间。
    /// </summary>
    private bool RoutePacketWithBackpressure(
        FFmpegPacketQueue queue, FFmpegPacketLease packet, CancellationToken cancellationToken)
    {
        if (_dropPacketsForLowLatency)
        {
            return queue.TryWriteLatest(packet);
        }

        while (!_disposed && !cancellationToken.IsCancellationRequested)
        {
            if (queue.TryWriteOrderedWithoutDrop(packet))
                return true;
            if (!_seekRequests.IsEmpty)
            {
                packet.Dispose();
                return false;
            }

            Thread.Sleep(2);
        }
        packet.Dispose();
        return false;
    }

    /// <summary>
    /// 结束尚未处理的解复用跳转请求。
    /// </summary>
    private void CompletePendingSeekRequests(Exception exception)
    {
        while (_seekRequests.TryDequeue(out var seek))
        {
            if (exception is OperationCanceledException)
            {
                seek.TryCancel();
            }
            else
            {
                seek.TrySetException(exception);
            }
        }
    }

    /// <summary>
    /// 查找指定媒体类型的流索引。
    /// </summary>
    private static FFmpegStreamDescriptor? FindStream(AVFormatContext* formatContext, AVMediaType mediaType)
    {
        for (var index = 0; index < formatContext->nb_streams; index++)
        {
            var stream = formatContext->streams[index];
            if (stream != null && stream->codecpar != null && stream->codecpar->codec_type == mediaType)
            {
                return new FFmpegStreamDescriptor(index, mediaType, stream->time_base, stream->avg_frame_rate, stream->r_frame_rate, MediaStartTime(formatContext, stream), stream->codecpar);
            }
        }
        return null;
    }

    /// <summary>
    /// 计算媒体的原始起始时间。
    /// </summary>
    private static TimeSpan MediaStartTime(AVFormatContext* formatContext, AVStream* stream)
    {
        if (formatContext->start_time != ffmpeg.AV_NOPTS_VALUE)
        {
            var formatSeconds = formatContext->start_time / (double)ffmpeg.AV_TIME_BASE;
            if (!double.IsNaN(formatSeconds) && !double.IsInfinity(formatSeconds))
            {
                return TimeSpan.FromSeconds(Math.Max(0, formatSeconds));
            }
        }

        if (stream->start_time == ffmpeg.AV_NOPTS_VALUE)
        {
            return TimeSpan.Zero;
        }

        var seconds = stream->start_time * ffmpeg.av_q2d(stream->time_base);
        return double.IsNaN(seconds) || double.IsInfinity(seconds) ? TimeSpan.Zero : TimeSpan.FromSeconds(Math.Max(0, seconds));
    }

    /// <summary>
    /// 对象已释放时阻止继续操作。
    /// </summary>
    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(FFmpegSharedDemuxer));
        }
    }

    /// <summary>
    /// 根据帧率估算视频包时长。
    /// </summary>
    private static TimeSpan EstimateVideoPacketDuration(FFmpegStreamDescriptor stream)
    {
        var fps = RationalToDouble(stream.AverageFrameRate);
        if (fps <= 0)
        {
            fps = RationalToDouble(stream.RealFrameRate);
        }

        return fps > 0 && !double.IsInfinity(fps) ? TimeSpan.FromSeconds(1d / fps) : TimeSpan.FromMilliseconds(33);
    }

    /// <summary>
    /// 根据音频参数估算单包时长。
    /// </summary>
    private static TimeSpan EstimateAudioPacketDuration(AVFormatContext* formatContext, FFmpegStreamDescriptor stream)
    {
        var avStream = formatContext->streams[stream.StreamIndex];
        var codecParameters = stream.CodecParameters;
        if (avStream != null && avStream->duration > 0 && codecParameters->sample_rate > 0 && codecParameters->frame_size > 0)
        {
            return TimeSpan.FromSeconds(codecParameters->frame_size / (double)codecParameters->sample_rate);
        }

        return TimeSpan.FromMilliseconds(20);
    }

    /// <summary>
    /// 将有理数转换为浮点值。
    /// </summary>
    private static double RationalToDouble(AVRational rational)
    {
        return rational.num > 0 && rational.den > 0 ? rational.num / (double)rational.den : 0;
    }
}
