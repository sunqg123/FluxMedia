using FFmpeg.AutoGen;
using FluxMedia.Core.Configuration;
using FluxMedia.Core.Logging;
using FluxMedia.Core.Models;
using FluxMedia.FFmpeg.Native;
using FluxMedia.Playback;
using FluxMedia.Playback.Internal;
using System.Collections.Concurrent;
using System.Diagnostics;
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
    /// 是否等待播放开始后再读取数据包。
    /// </summary>
    private readonly bool _deferPacketReading;

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
    /// 保护录制器及预滚缓存的锁。
    /// </summary>
    private readonly object _recordingSync = new();

    /// <summary>
    /// 保护单次资源释放的锁。
    /// </summary>
    private readonly object _disposeSync = new();

    /// <summary>
    /// 保护跨线程读取的状态、错误和最近包时刻。
    /// </summary>
    private readonly object _diagnosticSync = new();

    /// <summary>
    /// 录制开始前缓存的关键帧及后续数据包。
    /// </summary>
    private readonly List<FFmpegPacketLease> _recordingPrerollPackets = new();

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
    /// 录制。
    /// </summary>
    private FFmpegPacketRemuxRecorder? _recording;

    /// <summary>
    /// 待处理录制配置。
    /// </summary>
    private PlaybackRecordingOptions? _pendingRecordingOptions;

    /// <summary>
    /// 最近数据包时刻。
    /// </summary>
    private DateTimeOffset? _lastPacketAt;

    /// <summary>
    /// 状态。
    /// </summary>
    private string? _state;

    /// <summary>
    /// 退出原因。
    /// </summary>
    private string? _exitReason;

    /// <summary>
    /// 最近错误。
    /// </summary>
    private string? _lastError;

    /// <summary>
    /// 媒体读取发生超时的次数。
    /// </summary>
    private int _readTimeoutCount;

    /// <summary>
    /// 录制预滚字节。
    /// </summary>
    private long _recordingPrerollBytes;

    /// <summary>
    /// 预滚视频关键帧的时间（微秒）。
    /// </summary>
    private long? _recordingPrerollVideoKeyUs;

    /// <summary>
    /// 是否已请求释放解复用器。
    /// </summary>
    private volatile bool _disposed;

    /// <summary>
    /// 是否已进入单次资源清理。
    /// </summary>
    private bool _resourcesDisposed;

    /// <summary>
    /// 最大录制预滚字节。
    /// </summary>
    private const long MaxRecordingPrerollBytes = 64L * 1024 * 1024;

    /// <summary>
    /// 初始化 FFmpeg 共享解复用器所需的状态和资源。
    /// </summary>
    public FFmpegSharedDemuxer(MediaSource source, PlaybackOptions options, Guid instanceId, TimeSpan startPosition, int videoQueueCapacity = 4, int audioQueueCapacity = 24, bool deferPacketReading = false)
    {
        _source = source;
        _options = options;
        _instanceId = instanceId;
        _startPosition = startPosition;
        _deferPacketReading = deferPacketReading;
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
    public FFmpegStreamDescriptor? VideoStream { get; private set; }

    /// <summary>
    /// 音频流。
    /// </summary>
    public FFmpegStreamDescriptor? AudioStream { get; private set; }

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
    /// 读取数据包数量。
    /// </summary>
    public long ReadPacketCount => Interlocked.Read(ref _readPacketCount);

    /// <summary>
    /// 原生读取成功的数据包数。
    /// </summary>
    private long _readPacketCount;

    /// <summary>
    /// 已分发视频包数量。
    /// </summary>
    public long RoutedVideoPacketCount => Interlocked.Read(ref _routedVideoPacketCount);

    /// <summary>
    /// 已送入视频队列的数据包数。
    /// </summary>
    private long _routedVideoPacketCount;

    /// <summary>
    /// 已分发音频包数量。
    /// </summary>
    public long RoutedAudioPacketCount => Interlocked.Read(ref _routedAudioPacketCount);

    /// <summary>
    /// 已送入音频队列的数据包数。
    /// </summary>
    private long _routedAudioPacketCount;

    /// <summary>
    /// 已丢弃视频包数量。
    /// </summary>
    public long DroppedVideoPacketCount => _videoPackets.DroppedPackets;

    /// <summary>
    /// 已丢弃音频包数量。
    /// </summary>
    public long DroppedAudioPacketCount => _audioPackets.DroppedPackets;

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
    /// 录制诊断。
    /// </summary>
    public PlaybackRecordingDiagnosticInfo? RecordingDiagnostic
    {
        get { lock (_recordingSync) return _recording?.Diagnostic; }
    }

    /// <summary>
    /// 最近数据包时刻。
    /// </summary>
    public DateTimeOffset? LastPacketAt { get { lock (_diagnosticSync) return _lastPacketAt; } }

    /// <summary>
    /// 当前运行状态。
    /// </summary>
    public string? State { get { lock (_diagnosticSync) return _state; } }

    /// <summary>
    /// 退出原因。
    /// </summary>
    public string? ExitReason { get { lock (_diagnosticSync) return _exitReason; } }

    /// <summary>
    /// 最近错误。
    /// </summary>
    public string? LastError { get { lock (_diagnosticSync) return _lastError; } }

    /// <summary>
    /// 读取超时数量。
    /// </summary>
    public int ReadTimeoutCount => Volatile.Read(ref _readTimeoutCount);

    /// <summary>
    /// 一次读取运行状态；各包计数使用原子读取。
    /// </summary>
    public PlaybackNetworkDiagnosticInfo GetNetworkDiagnostic()
    {
        lock (_diagnosticSync)
        {
            return new PlaybackNetworkDiagnosticInfo
            {
                IsNetworkSource = _source.Kind == MediaSourceKind.NetworkStream,
                ReadTimeoutCount = ReadTimeoutCount,
                LastPacketAt = _lastPacketAt,
                DemuxerState = _state,
                DemuxerExitReason = _exitReason,
                LastNetworkError = _lastError,
                DroppedVideoPacketCount = _videoPackets.DroppedPackets,
                DroppedAudioPacketCount = _audioPackets.DroppedPackets,
                SeekDiscardedVideoPacketCount = _videoPackets.SeekDiscardedPackets,
                SeekDiscardedAudioPacketCount = _audioPackets.SeekDiscardedPackets,
                StopDiscardedVideoPacketCount = _videoPackets.StopDiscardedPackets,
                StopDiscardedAudioPacketCount = _audioPackets.StopDiscardedPackets
            };
        }
    }

    /// <summary>
    /// 媒体信息。
    /// </summary>
    public MediaInfo? MediaInfo { get; private set; }

    /// <summary>
    /// 当前原生读取位置对应的数据包代次。
    /// </summary>
    private long _packetGeneration;

    /// <summary>
    /// 供解码器判断手中数据包是否已经过期。
    /// </summary>
    public long PacketGeneration => Interlocked.Read(ref _packetGeneration);

    /// <summary>
    /// 打开诊断。
    /// </summary>
    public PlaybackDiagnosticInfo? OpenDiagnostic { get; private set; }

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
        lock (_diagnosticSync)
        {
            _state = "Starting";
            _exitReason = null;
            _lastError = null;
        }
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
        try { _lifetimeCts?.Cancel(); }
        catch (Exception exception)
        {
            FluxMediaLog.Write(LogLevel.Error, nameof(FFmpegSharedDemuxer), _instanceId, "Event=DemuxerCancelFailed", exception);
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
            if (_disposed) return;
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
            try { demuxer.DisposeResources(); }
            catch (Exception exception)
            {
                FluxMediaLog.Write(LogLevel.Error, nameof(FFmpegSharedDemuxer), demuxer._instanceId, "Event=DeferredDemuxerCleanupFailed", exception);
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
            if (_resourcesDisposed) return;
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
        Release(() => recording?.DisposeAsync().AsTask().GetAwaiter().GetResult());
        Release(() => VideoStream?.Dispose());
        Release(() => AudioStream?.Dispose());
        Release(() => _lifetimeCts?.Dispose());
        if (errors.Count > 0) throw new AggregateException("解复用资源释放失败。", errors);

        // 每项资源均尝试清理，最后统一报告异常。
        void Release(Action release)
        {
            try { release(); }
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
        var exitReason = "Completed";

        try
        {
            lock (_diagnosticSync) _state = "Opening";
            interrupt = PlaybackFFmpegOptions.CreateInterruptContext(_source.Kind == MediaSourceKind.NetworkStream ? PlaybackFFmpegOptions.EffectiveOpenTimeout(_options) : _options.OpenTimeout, cancellationToken);
            dictionary = PlaybackFFmpegOptions.CreateOpenDictionary(_source, _options, _instanceId);
            formatContext = ffmpeg.avformat_alloc_context();
            if (formatContext == null)
            {
                throw new InvalidOperationException("FFmpeg format context allocation failed.");
            }

            formatContext->interrupt_callback = interrupt.Callback;
            FluxMediaLog.Write(LogLevel.Information, nameof(FFmpegSharedDemuxer), _instanceId, $"Event=SharedDemuxOpenInputStart SourceKind={_source.Kind} Url={_source.Uri} FFmpegOpenOptions={PlaybackFFmpegOptions.Describe(_source, _options)}");
            var openStartedAt = Stopwatch.GetTimestamp();
            var openResult = ffmpeg.avformat_open_input(&formatContext, _source.Uri, null, &dictionary);
            var openElapsed = PlaybackClock.ElapsedSince(openStartedAt);
            FluxMediaLog.Write(LogLevel.Information, nameof(FFmpegSharedDemuxer), _instanceId, $"Event=SharedDemuxOpenInputCompleted Result={openResult} Error={FormatError(openResult) ?? "-"} OpenElapsedMs={openElapsed.TotalMilliseconds:0}");
            FFmpegErrorHelper.ThrowIfError(openResult, "avformat_open_input");

            interrupt.ResetDeadline();
            FluxMediaLog.Write(LogLevel.Information, nameof(FFmpegSharedDemuxer), _instanceId, $"Event=SharedDemuxFindStreamInfoStart SourceKind={_source.Kind} Url={_source.Uri}");
            var streamInfoStartedAt = Stopwatch.GetTimestamp();
            var streamInfoResult = ffmpeg.avformat_find_stream_info(formatContext, null);
            var streamInfoElapsed = PlaybackClock.ElapsedSince(streamInfoStartedAt);
            FluxMediaLog.Write(LogLevel.Information, nameof(FFmpegSharedDemuxer), _instanceId, $"Event=SharedDemuxFindStreamInfoCompleted Result={streamInfoResult} Error={FormatError(streamInfoResult) ?? "-"} StreamInfoElapsedMs={streamInfoElapsed.TotalMilliseconds:0} Streams={formatContext->nb_streams}");
            FFmpegErrorHelper.ThrowIfError(streamInfoResult, "avformat_find_stream_info");

            VideoStream = FindStream(formatContext, AVMediaType.AVMEDIA_TYPE_VIDEO);
            AudioStream = FindStream(formatContext, AVMediaType.AVMEDIA_TYPE_AUDIO);
            MediaInfo = FFmpegMediaInfoReader.BuildMediaInfo(formatContext);
            var timingMode = PlaybackTimingPolicy.Resolve(_source, _options, MediaInfo.Duration);
            TimingMode = timingMode;
            _dropPacketsForLowLatency = PlaybackTimingPolicy.IsLiveNetworkSource(_source, MediaInfo.Duration) && _options.Network.LowLatencyMode && timingMode == PlaybackTimingMode.RealtimeLowLatency;
            OpenDiagnostic = new PlaybackDiagnosticInfo
            {
                SourceKind = _source.Kind,
                SourceUrl = _source.Uri,
                FFmpegOpenOptions = PlaybackFFmpegOptions.Describe(_source, _options),
                OpenElapsed = openElapsed,
                StreamInfoElapsed = streamInfoElapsed,
                Protocol = MediaSourcePolicy.GetProtocol(_source),
                Container = MediaInfo.ContainerFormat,
                FrameRate = MediaInfo.VideoFrameRate,
                TimingMode = timingMode,
                LowLatencyEnabled = timingMode == PlaybackTimingMode.RealtimeLowLatency,
                CanSeek = PlaybackTimingPolicy.CanSeek(_source, MediaInfo.Duration),
                IsLive = PlaybackTimingPolicy.IsLiveNetworkSource(_source, MediaInfo.Duration),
                HasDuration = MediaInfo.Duration > TimeSpan.Zero
            };
            if (VideoStream is not null)
            {
                _videoPackets.ConfigureTiming(VideoStream.TimeBase, EstimateVideoPacketDuration(VideoStream), VideoStream.StartTime);
            }

            if (AudioStream is not null)
            {
                _audioPackets.ConfigureTiming(AudioStream.TimeBase, EstimateAudioPacketDuration(formatContext, AudioStream), AudioStream.StartTime);
            }

            StartPendingRecordingIfNeeded();
            lock (_diagnosticSync) _state = "Running";
            _ready?.TrySetResult(true);
            var readGateStartedAt = Stopwatch.GetTimestamp();
            FluxMediaLog.Write(LogLevel.Information, nameof(FFmpegSharedDemuxer), _instanceId, $"Event=SharedDemuxReadGateWaiting DeferredPacketReading={_deferPacketReading}");
            _readGate.Wait(cancellationToken);
            FluxMediaLog.Write(LogLevel.Information, nameof(FFmpegSharedDemuxer), _instanceId, $"Event=SharedDemuxReadGateReleased WaitElapsedMs={PlaybackClock.ElapsedSince(readGateStartedAt).TotalMilliseconds:0}");

            if (_startPosition > TimeSpan.Zero)
            {
                interrupt.ResetDeadline();
                SeekFormatContext(formatContext, _startPosition);
            }

            if (_source.Kind == MediaSourceKind.NetworkStream)
            {
                interrupt.SetTimeout(PlaybackFFmpegOptions.EffectiveReadTimeout(_options));
            }

            packet = ffmpeg.av_packet_alloc();
            if (packet == null)
            {
                throw new InvalidOperationException("FFmpeg packet allocation failed.");
            }

            FluxMediaLog.Write(LogLevel.Information, nameof(FFmpegSharedDemuxer), _instanceId, $"Event=SharedDemuxStart SourceKind={_source.Kind} Url={_source.Uri} VideoStream={VideoStream?.StreamIndex.ToString() ?? "-"} AudioStream={AudioStream?.StreamIndex.ToString() ?? "-"}");

            var packetReadStartedAt = Stopwatch.GetTimestamp();
            var firstPacketLogged = false;
            var firstVideoPacketLogged = false;
            var firstVideoKeyPacketLogged = false;
            Func<bool> seekPending = () => !_seekRequests.IsEmpty;
            while (!_disposed && !cancellationToken.IsCancellationRequested)
            {
                while (_seekRequests.TryDequeue(out var seek))
                {
                    if (seek.IsCompleted) continue;
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
                        if (read != ffmpeg.AVERROR(ffmpeg.EAGAIN) || interrupt.ShouldInterrupt()) break;
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
                        FluxMediaLog.Write(LogLevel.Debug, nameof(FFmpegSharedDemuxer), _instanceId, "Event=ReadInterruptedForSeek");
                        continue;
                    }
                    var timedOut = interrupt.TimedOut && !cancellationToken.IsCancellationRequested && !_disposed;
                    if (cancellationToken.IsCancellationRequested || _disposed)
                    {
                        exitReason = "Canceled";
                        break;
                    }
                    exitReason = timedOut ? "ReadTimeout" : $"av_read_frame returned {read}";
                    if (timedOut)
                    {
                        Interlocked.Increment(ref _readTimeoutCount);
                        lock (_diagnosticSync) _lastError = exitReason;
                        throw new TimeoutException($"FFmpeg shared demux read timed out: {_source.Uri}");
                    }

                    if (read == ffmpeg.AVERROR_EOF && !PlaybackTimingPolicy.IsLiveNetworkSource(_source, MediaInfo.Duration))
                    {
                        exitReason = "Completed";
                        break;
                    }
                    // 直播 EOF 和非 EOF 读取错误均不能伪装为自然结束。
                    throw new IOException($"Media read interrupted ({read}): {FormatError(read)}");
                }

                try
                {
                    Interlocked.Increment(ref _readPacketCount);
                    lock (_diagnosticSync) _lastPacketAt = DateTimeOffset.UtcNow;
                    if (!firstPacketLogged)
                    {
                        firstPacketLogged = true;
                        LogFirstPacket("SharedDemuxFirstPacket", packet, PlaybackClock.ElapsedSince(packetReadStartedAt));
                    }

                    if (VideoStream is not null && packet->stream_index == VideoStream.StreamIndex)
                    {
                        if (!firstVideoPacketLogged)
                        {
                            firstVideoPacketLogged = true;
                            LogFirstPacket("SharedDemuxFirstVideoPacket", packet, PlaybackClock.ElapsedSince(packetReadStartedAt));
                        }

                        if (!firstVideoKeyPacketLogged &&
                            (packet->flags & ffmpeg.AV_PKT_FLAG_KEY) != 0)
                        {
                            firstVideoKeyPacketLogged = true;
                            LogFirstPacket("SharedDemuxFirstVideoKeyPacket", packet, PlaybackClock.ElapsedSince(packetReadStartedAt));
                        }

                        if (RoutePacketWithBackpressure(_videoPackets, FFmpegPacketLease.Clone(packet, PacketGeneration), cancellationToken))
                        {
                            Interlocked.Increment(ref _routedVideoPacketCount);
                        }
                    }
                    else if (AudioStream is not null && packet->stream_index == AudioStream.StreamIndex)
                    {
                        if (RoutePacketWithBackpressure(_audioPackets, FFmpegPacketLease.Clone(packet, PacketGeneration), cancellationToken))
                        {
                            Interlocked.Increment(ref _routedAudioPacketCount);
                        }
                    }
                }
                finally
                {
                    ffmpeg.av_packet_unref(packet);
                }
            }
            if (_disposed || cancellationToken.IsCancellationRequested) exitReason = "Canceled";
        }
        catch (Exception exception) when (cancellationToken.IsCancellationRequested || _disposed)
        {
            exitReason = "Canceled";
            _ready?.TrySetCanceled();
            throw new OperationCanceledException("媒体读取已取消。", exception, cancellationToken);
        }
        catch (Exception ex)
        {
            exitReason = ex.Message;
            lock (_diagnosticSync)
            {
                _state = "Faulted";
                _lastError = ex.Message;
            }
            _ready?.TrySetException(ex);
            CompletePendingSeekRequests(ex);
            _videoPackets.Complete(ex);
            _audioPackets.Complete(ex);
            FluxMediaLog.Write(LogLevel.Error, nameof(FFmpegSharedDemuxer), _instanceId, $"Event=SharedDemuxError Source={_source.Uri} Error={ex.Message}", ex);
            throw;
        }
        finally
        {
            CompletePendingSeekRequests(new OperationCanceledException("The demuxer has exited."));
            lock (_diagnosticSync)
            {
                _exitReason = exitReason;
                if (_state != "Faulted") _state = exitReason == "Completed" ? "Completed" : "Exited";
            }
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
            FluxMediaLog.Write(
                LogLevel.Information, nameof(FFmpegSharedDemuxer), _instanceId, $"Event=SharedDemuxExit Reason={exitReason} ReadPackets={ReadPacketCount} VideoPackets={RoutedVideoPacketCount} AudioPackets={RoutedAudioPacketCount} DroppedVideo={DroppedVideoPacketCount} DroppedAudio={DroppedAudioPacketCount}");
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
        FluxMediaLog.Write(
            result < 0 ? LogLevel.Warning : LogLevel.Information, nameof(FFmpegSharedDemuxer), _instanceId, $"Event=SharedDemuxRuntimeSeek Target={target} Result={result} FFmpegErrorText={FormatError(result) ?? "-"}");
        FFmpegErrorHelper.ThrowIfError(result, "av_seek_frame");
        var generation = Interlocked.Increment(ref _packetGeneration);
        FluxMediaLog.Write(LogLevel.Information, nameof(FFmpegSharedDemuxer), _instanceId,
            $"Event=SeekPacketGeneration Generation={generation} Target={target}");
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
            if (queue.TryWriteOrderedWithoutDrop(packet)) return true;
            if (!_seekRequests.IsEmpty)
            {
                queue.Discard(packet, forSeek: true);
                return false;
            }

            Thread.Sleep(2);
        }
        queue.Discard(packet, forSeek: false);
        return false;
    }

    /// <summary>
    /// 记录首个数据包的时间和流信息。
    /// </summary>
    private void LogFirstPacket(string eventName, AVPacket* packet, TimeSpan elapsed)
    {
        var streamKind = PacketStreamKind(packet);
        var timestampUs = PacketTimestampUs(packet, PacketTimeBase(packet));
        FluxMediaLog.Write(
            LogLevel.Information, nameof(FFmpegSharedDemuxer), _instanceId, $"Event={eventName} SourceKind={_source.Kind} Url={_source.Uri} WaitElapsedMs={elapsed.TotalMilliseconds:0} StreamIndex={packet->stream_index} StreamKind={streamKind} IsKey={(packet->flags & ffmpeg.AV_PKT_FLAG_KEY) != 0} Pts={FormatTimestampValue(packet->pts)} Dts={FormatTimestampValue(packet->dts)} TimestampUs={FormatNullableTimestamp(timestampUs)} Size={packet->size}");
    }

    /// <summary>
    /// 获取数据包所属的媒体类型。
    /// </summary>
    private string PacketStreamKind(AVPacket* packet)
    {
        if (VideoStream is not null && packet->stream_index == VideoStream.StreamIndex)
        {
            return "Video";
        }

        if (AudioStream is not null && packet->stream_index == AudioStream.StreamIndex)
        {
            return "Audio";
        }

        return "Other";
    }

    /// <summary>
    /// 获取数据包所属流的时间基。
    /// </summary>
    private AVRational PacketTimeBase(AVPacket* packet)
    {
        if (VideoStream is not null && packet->stream_index == VideoStream.StreamIndex)
        {
            return VideoStream.TimeBase;
        }

        if (AudioStream is not null && packet->stream_index == AudioStream.StreamIndex)
        {
            return AudioStream.TimeBase;
        }

        return new AVRational { num = 1, den = ffmpeg.AV_TIME_BASE };
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
    /// 启动当前媒体的包级录制。
    /// </summary>
    public void StartRecording(PlaybackRecordingOptions options)
    {
        ThrowIfDisposed();
        _pendingRecordingOptions = options;
        if (VideoStream is null && AudioStream is null) return;
        StartPendingRecordingIfNeeded();
    }

    /// <summary>
    /// 将已消费的播放数据包交给录制器。
    /// </summary>
    public void WriteRecordingPacket(nint packet)
    {
        FFmpegPacketRemuxRecorder? recording;
        lock (_recordingSync)
        {
            recording = _recording;
            if (recording is null)
            {
                CacheRecordingPreroll((AVPacket*)packet);
                return;
            }
        }
        recording?.TryWrite((AVPacket*)packet);
    }

    /// <summary>
    /// 时间线跳变前暂停录制写入。
    /// </summary>
    public void SuspendRecordingForDiscontinuity()
    {
        FFmpegPacketRemuxRecorder? recording;
        lock (_recordingSync)
        {
            recording = _recording;
        }
        recording?.SuspendForDiscontinuity();
    }

    /// <summary>
    /// 时间线跳变后恢复录制写入。
    /// </summary>
    public void ResumeRecordingAfterDiscontinuity()
    {
        FFmpegPacketRemuxRecorder? recording;
        lock (_recordingSync)
        {
            recording = _recording;
        }
        recording?.ResumeAfterDiscontinuity();
    }

    /// <summary>
    /// 解除录制关联并返回录制结果。
    /// </summary>
    public FFmpegPacketRemuxRecorder? DetachRecording()
    {
        lock (_recordingSync)
        {
            var recording = _recording;
            _recording = null;
            return recording;
        }
    }

    /// <summary>
    /// 关联当前录制器并准备写入播放数据包。
    /// </summary>
    public void AttachRecording(FFmpegPacketRemuxRecorder recording)
    {
        if (recording is null)
        {
            throw new ArgumentNullException(nameof(recording));
        }
        ThrowIfDisposed();
        lock (_recordingSync)
        {
            if (_recording is not null)
            {
                throw new InvalidOperationException("A recording is already attached to this demuxer.");
            }

            _recording = recording;
            _pendingRecordingOptions = null;
            FlushRecordingPreroll(recording);
        }
    }

    /// <summary>
    /// 具备流信息后启动等待中的录制。
    /// </summary>
    private void StartPendingRecordingIfNeeded()
    {
        var options = _pendingRecordingOptions;
        if (options is null || _recording is not null) return;
        if (VideoStream is null && AudioStream is null)
        {
            throw new InvalidOperationException("No audio or video streams are available for recording.");
        }

        var recording = new FFmpegPacketRemuxRecorder(options, _instanceId, VideoStream, AudioStream);
        recording.Start();
        lock (_recordingSync)
        {
            _recording = recording;
            _pendingRecordingOptions = null;
            FlushRecordingPreroll(recording);
        }
    }

    /// <summary>
    /// 缓存录制所需的起始关键帧及相邻数据包。
    /// </summary>
    private void CacheRecordingPreroll(AVPacket* packet)
    {
        if (_source.Kind != MediaSourceKind.File || packet == null || VideoStream is null) return;
        if (packet->stream_index == VideoStream.StreamIndex)
        {
            if ((packet->flags & ffmpeg.AV_PKT_FLAG_KEY) != 0)
            {
                ClearRecordingPreroll();
                _recordingPrerollVideoKeyUs = PacketTimestampUs(packet, VideoStream.TimeBase);
            }

            if (_recordingPrerollVideoKeyUs is null) return;
        }
        else if (AudioStream is not null && packet->stream_index == AudioStream.StreamIndex)
        {
            if (_recordingPrerollVideoKeyUs is null) return;
            var audioTimestampUs = PacketTimestampUs(packet, AudioStream.TimeBase);
            if (audioTimestampUs is not null && audioTimestampUs < _recordingPrerollVideoKeyUs) return;
        }
        else
        {
            return;
        }

        var clone = FFmpegPacketLease.Clone(packet, PacketGeneration);
        _recordingPrerollPackets.Add(clone);
        _recordingPrerollBytes += Math.Max(0, clone.Packet->size);
        if (_recordingPrerollBytes > MaxRecordingPrerollBytes)
        {
            ClearRecordingPreroll();
        }
    }

    /// <summary>
    /// 将录制预滚包写入输出文件。
    /// </summary>
    private void FlushRecordingPreroll(FFmpegPacketRemuxRecorder recording)
    {
        if (_recordingPrerollPackets.Count == 0) return;
        var packets = _recordingPrerollPackets.OrderBy(packet => RecordingPacketTimestampUs(packet.Packet)).ToArray();
        _recordingPrerollPackets.Clear();
        _recordingPrerollBytes = 0;
        _recordingPrerollVideoKeyUs = null;
        foreach (var packet in packets)
        {
            using (packet)
            {
                recording.TryWrite(packet.Packet);
            }
        }

        FluxMediaLog.Write(
            LogLevel.Information, nameof(FFmpegSharedDemuxer), _instanceId, $"Event=RecordingPrerollFlushed Packets={packets.Length} Source={_source.Uri}");
    }

    /// <summary>
    /// 获取录制使用的数据包时间戳。
    /// </summary>
    private long RecordingPacketTimestampUs(AVPacket* packet)
    {
        var timeBase = VideoStream is not null && packet->stream_index == VideoStream.StreamIndex
            ? VideoStream.TimeBase
            : AudioStream?.TimeBase ?? new AVRational { num = 1, den = ffmpeg.AV_TIME_BASE };
        return PacketTimestampUs(packet, timeBase) ?? long.MaxValue;
    }

    /// <summary>
    /// 将数据包时间戳换算为微秒。
    /// </summary>
    private static long? PacketTimestampUs(AVPacket* packet, AVRational timeBase)
    {
        var timestamp = packet->dts != ffmpeg.AV_NOPTS_VALUE ? packet->dts : packet->pts;
        return timestamp == ffmpeg.AV_NOPTS_VALUE
            ? null
            : ffmpeg.av_rescale_q(timestamp, timeBase, new AVRational { num = 1, den = ffmpeg.AV_TIME_BASE });
    }

    /// <summary>
    /// 释放录制预滚缓存。
    /// </summary>
    private void ClearRecordingPreroll()
    {
        foreach (var packet in _recordingPrerollPackets)
        {
            packet.Dispose();
        }

        _recordingPrerollPackets.Clear();
        _recordingPrerollBytes = 0;
        _recordingPrerollVideoKeyUs = null;
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
    /// 将原生错误码转换为可读信息。
    /// </summary>
    private static string? FormatError(int code) => code < 0 ? FFmpegErrorHelper.GetErrorMessage(code) : null;

    /// <summary>
    /// 将原生时间戳转换为日志文本。
    /// </summary>
    private static string FormatTimestampValue(long timestamp) => timestamp == ffmpeg.AV_NOPTS_VALUE ? "-" : timestamp.ToString();

    /// <summary>
    /// 将可空时间戳转换为日志文本。
    /// </summary>
    private static string FormatNullableTimestamp(long? timestamp) => timestamp?.ToString() ?? "-";

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
