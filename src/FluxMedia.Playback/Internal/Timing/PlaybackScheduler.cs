namespace FluxMedia.Playback.Internal;

/// <summary>
/// 依据音频时钟和缓冲量调度视频帧。
/// </summary>
internal sealed class PlaybackScheduler
{
    /// <summary>
    /// 视频丢弃容差。
    /// </summary>
    private static readonly TimeSpan VideoDropTolerance = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// 视频同步容差。
    /// </summary>
    private static readonly TimeSpan VideoSyncTolerance = TimeSpan.FromMilliseconds(2);

    /// <summary>
    /// 最大音频同步等待。
    /// </summary>
    private static readonly TimeSpan MaxAudioSyncWait = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// 最大重新缓冲等待。
    /// </summary>
    private static readonly TimeSpan MaxRebufferWait = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// 重新缓冲轮询间隔。
    /// </summary>
    private static readonly TimeSpan RebufferPollInterval = TimeSpan.FromMilliseconds(20);

    /// <summary>
    /// 时序模式。
    /// </summary>
    private readonly PlaybackTimingMode _timingMode;

    /// <summary>
    /// 重新同步后先直接呈现一帧，再恢复常规调度。
    /// </summary>
    private bool _presentNextFrameImmediately;

    /// <summary>
    /// 读取当前播放速度的回调。
    /// </summary>
    private readonly Func<double> _playbackRate;

    /// <summary>
    /// 判断当前任务是否应停止的回调。
    /// </summary>
    private readonly Func<bool> _shouldStop;

    /// <summary>
    /// 读取最新音频诊断的回调。
    /// </summary>
    private readonly Func<AudioPlaybackDiagnostics?> _audioDiagnostics;

    /// <summary>
    /// 读取视频缓冲时长的回调。
    /// </summary>
    private readonly Func<TimeSpan> _videoBufferedDuration;

    /// <summary>
    /// 读取视频缓冲包数的回调。
    /// </summary>
    private readonly Func<int> _videoBufferedPacketCount;

    /// <summary>
    /// 首次显示前期望积累的最小缓冲时长。
    /// </summary>
    private readonly TimeSpan _minBufferDuration;

    /// <summary>
    /// 低延迟模式允许的最大视频积压时长。
    /// </summary>
    private readonly TimeSpan _maxLowLatencyDelay;

    /// <summary>
    /// 本轮调度采用的首个媒体时间戳。
    /// </summary>
    private TimeSpan? _firstMediaTimestamp;

    /// <summary>
    /// 本轮媒体时间映射到系统时间的起点。
    /// </summary>
    private DateTimeOffset? _presentationStartedAt;

    /// <summary>
    /// 上次媒体时钟使用的倍速。
    /// </summary>
    private double _clockRate = 1;

    /// <summary>
    /// 音频设备当前可听区间的倍速。
    /// </summary>
    private double _audioRate = 1;

    /// <summary>
    /// 本轮首次缓冲等待开始的时间。
    /// </summary>
    private DateTimeOffset? _bufferingStartedAt;

    /// <summary>
    /// 本轮调度是否已显示过视频帧。
    /// </summary>
    private bool _hasPresentedVideo;

    /// <summary>
    /// 初始化播放调度器所需的状态和资源。
    /// </summary>
    public PlaybackScheduler(PlaybackTimingMode timingMode, Func<double> playbackRate, Func<bool> shouldStop, Func<AudioPlaybackDiagnostics?>? audioDiagnostics = null, Func<TimeSpan>? videoBufferedDuration = null, Func<int>? videoBufferedPacketCount = null, TimeSpan? minBufferDuration = null, TimeSpan? maxLowLatencyDelay = null)
    {
        _timingMode = timingMode;
        _playbackRate = playbackRate;
        _shouldStop = shouldStop;
        _audioDiagnostics = audioDiagnostics ?? (() => null);
        _videoBufferedDuration = videoBufferedDuration ?? (() => TimeSpan.Zero);
        _videoBufferedPacketCount = videoBufferedPacketCount ?? (() => 0);
        _minBufferDuration = minBufferDuration ?? TimeSpan.FromMilliseconds(500);
        _maxLowLatencyDelay = maxLowLatencyDelay ?? TimeSpan.FromMilliseconds(60);
    }

    /// <summary>
    /// 时序模式。
    /// </summary>
    public PlaybackTimingMode TimingMode => _playbackRate() == 1.0 ? _timingMode : PlaybackTimingMode.PresentationTimestamp;

    /// <summary>
    /// 清除 Seek 时钟，目标首帧直接预览。
    /// </summary>
    public void Reset()
    {
        ResetPresentationClock();
        _bufferingStartedAt = null;
        _hasPresentedVideo = false;
        _presentNextFrameImmediately = true;
    }

    /// <summary>
    /// 根据缓冲和音频时钟决定当前帧的处理方式。
    /// </summary>
    public VideoFrameSchedulingDecision ScheduleVideoFrame(TimeSpan timestamp, TimeSpan fallbackFrameInterval, CancellationToken cancellationToken)
    {
        var bufferedDuration = _videoBufferedDuration();
        if (_presentNextFrameImmediately)
        {
            _presentNextFrameImmediately = false;
            _hasPresentedVideo = true;
            return new VideoFrameSchedulingDecision(VideoFrameSchedulingAction.Present, TimeSpan.Zero, null, null, bufferedDuration, false);
        }
        if (TimingMode == PlaybackTimingMode.RealtimeLowLatency)
        {
            if (_videoBufferedPacketCount() > 0 && bufferedDuration > _maxLowLatencyDelay)
            {
                return new VideoFrameSchedulingDecision(VideoFrameSchedulingAction.Drop, TimeSpan.Zero, null, null, bufferedDuration, true);
            }
        }
        else if (!_hasPresentedVideo &&
            _videoBufferedPacketCount() > 0 &&
            bufferedDuration > TimeSpan.Zero &&
            bufferedDuration < _minBufferDuration &&
            !HasRebufferTimedOut())
        {
            _bufferingStartedAt ??= DateTimeOffset.UtcNow;
            Wait(RebufferPollInterval, cancellationToken);
            return new VideoFrameSchedulingDecision(VideoFrameSchedulingAction.Buffer, RebufferPollInterval, null, null, bufferedDuration, false);
        }
        else
        {
            _bufferingStartedAt = null;
        }

        var audioClock = GetAudioClock();
        if (audioClock is not null)
        {
            ResetPresentationClock();
            var distance = timestamp - audioClock.Value;
            if (distance < -VideoDropTolerance)
            {
                return new VideoFrameSchedulingDecision(VideoFrameSchedulingAction.Drop, TimeSpan.Zero, distance, audioClock.Value, bufferedDuration, false);
            }

            if (distance > VideoSyncTolerance)
            {
                var wait = TimeSpan.FromTicks((long)(distance.Ticks / _audioRate));
                Wait(wait > MaxAudioSyncWait ? MaxAudioSyncWait : wait, cancellationToken);
                _hasPresentedVideo = true;
                return new VideoFrameSchedulingDecision(VideoFrameSchedulingAction.Present, distance, distance, audioClock.Value, bufferedDuration, false);
            }

            _hasPresentedVideo = true;
            return new VideoFrameSchedulingDecision(VideoFrameSchedulingAction.Present, TimeSpan.Zero, distance, audioClock.Value, bufferedDuration, false);
        }

        var delay = CalculatePresentationDelay(timestamp, fallbackFrameInterval);
        Wait(delay, cancellationToken);
        _hasPresentedVideo = true;
        return new VideoFrameSchedulingDecision(VideoFrameSchedulingAction.Present, delay, null, null, bufferedDuration, false);
    }

    /// <summary>
    /// 等待当前视频帧到达应显示的时间。
    /// </summary>
    public void WaitForVideoFrame(TimeSpan timestamp, TimeSpan fallbackFrameInterval, CancellationToken cancellationToken) => ScheduleVideoFrame(timestamp, fallbackFrameInterval, cancellationToken);

    /// <summary>
    /// 根据媒体时间和播放速度计算显示等待。
    /// </summary>
    private TimeSpan CalculatePresentationDelay(TimeSpan timestamp, TimeSpan fallbackFrameInterval)
    {
        var now = DateTimeOffset.UtcNow;
        var rate = PlaybackRatePolicy.Normalize(_playbackRate());
        if (rate != _clockRate && _firstMediaTimestamp is not null && _presentationStartedAt is not null)
        {
            // 从当前媒体位置重新锚定，避免变速后突然跳帧。
            _firstMediaTimestamp += TimeSpan.FromTicks((long)((now - _presentationStartedAt.Value).Ticks * _clockRate));
            _presentationStartedAt = now;
        }
        _clockRate = rate;
        return PlaybackPacer.CalculateDelay(
            timestamp, fallbackFrameInterval, TimingMode, ref _firstMediaTimestamp, ref _presentationStartedAt, now, rate);
    }

    /// <summary>
    /// 扣除设备缓冲和延迟，估算当前可听时间。
    /// </summary>
    private TimeSpan? GetAudioClock()
    {
        var audio = _audioDiagnostics();
        if (audio?.PresentationTime is null || audio.WorkerState != "Running" || audio.OutputBufferedDuration is null || audio.OutputBufferedDuration <= TimeSpan.Zero)
        {
            return null;
        }

        _audioRate = PlaybackRatePolicy.Normalize(audio.PlaybackRate);
        var buffered = audio.OutputBufferedDuration ?? TimeSpan.Zero;
        var deviceDelay = audio.OutputDeviceDelay ?? TimeSpan.Zero;
        var elapsed = audio.UpdatedAt == default ? TimeSpan.Zero : DateTimeOffset.UtcNow - audio.UpdatedAt;
        if (elapsed < TimeSpan.Zero || elapsed > TimeSpan.FromSeconds(2))
        {
            elapsed = TimeSpan.Zero;
        }

        var clock = audio.PresentationTime.Value + TimeSpan.FromTicks((long)(Math.Min(elapsed.TotalSeconds, (buffered + deviceDelay).TotalSeconds) * TimeSpan.TicksPerSecond * _audioRate));
        return clock < TimeSpan.Zero ? TimeSpan.Zero : clock;
    }

    /// <summary>
    /// 暂停或时钟切换后重新锚定显示时间。
    /// </summary>
    public void ResetPresentationClock()
    {
        _firstMediaTimestamp = null;
        _presentationStartedAt = null;
        _clockRate = PlaybackRatePolicy.Normalize(_playbackRate());
    }

    /// <summary>
    /// 判断本次缓冲等待是否已达到上限。
    /// </summary>
    private bool HasRebufferTimedOut()
    {
        return _bufferingStartedAt is not null && DateTimeOffset.UtcNow - _bufferingStartedAt.Value >= MaxRebufferWait;
    }

    /// <summary>
    /// 分段等待，及时响应取消和停止。
    /// </summary>
    private void Wait(TimeSpan delay, CancellationToken cancellationToken)
    {
        if (delay <= TimeSpan.Zero)
        {
            return;
        }

        var dueAt = DateTimeOffset.UtcNow + delay;
        while (delay > TimeSpan.Zero && !_shouldStop() && !cancellationToken.IsCancellationRequested)
        {
            Thread.Sleep(delay > TimeSpan.FromMilliseconds(20) ? 20 : Math.Max(1, (int)delay.TotalMilliseconds));
            delay = dueAt - DateTimeOffset.UtcNow;
        }
    }
}
