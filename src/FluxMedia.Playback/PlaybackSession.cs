using FluxMedia.Playback.Internal.Audio;
using FluxMedia.Core.Configuration;
using FluxMedia.Core.Errors;
using FluxMedia.Core.Logging;
using FluxMedia.Core.Models;
using FluxMedia.Core.State;
using FluxMedia.FFmpeg.Pipeline;
using FluxMedia.Playback.Internal;

namespace FluxMedia.Playback;

/// <summary>
/// 管理媒体打开、播放控制和音视频输出。
/// </summary>
internal sealed class PlaybackSession : AsyncMediaStateMachine<PlaybackState>, IPlaybackSession
{

    /// <summary>
    /// 首次界面恢复超时。
    /// </summary>
    private static readonly TimeSpan FirstUiRecoveryTimeout = TimeSpan.FromMilliseconds(1500);

    /// <summary>
    /// 位置通知间隔。
    /// </summary>
    private static readonly TimeSpan PositionNotificationInterval = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// 创建会话时保存的默认配置。
    /// </summary>
    private readonly PlaybackOptions _defaultOptions;

    /// <summary>
    /// 传给音视频解码线程的同步信号。
    /// </summary>
    private readonly PlaybackResyncSignal _resyncSignal = new();

    /// <summary>
    /// 限制播放进度通知频率的控制器。
    /// </summary>
    private readonly PlaybackPositionNotificationGate _positionNotifications = new(PositionNotificationInterval);

    /// <summary>
    /// 串行执行播放管线变更的锁。
    /// </summary>
    private readonly SemaphoreSlim _pipelineMutationGate = new(1, 1);

    /// <summary>
    /// 取消跳转和管线恢复操作的信号源。
    /// </summary>
    private CancellationTokenSource? _operationCts;

    /// <summary>
    /// 停止请求版本，防止等待锁的动作复活旧操作。
    /// </summary>
    private int _cancelVersion;

    /// <summary>
    /// 安全读取当前操作令牌，已退役的操作按取消处理。
    /// </summary>
    private CancellationToken OperationToken
    {
        get
        {
            var source = _operationCts;
            if (source is null) return new CancellationToken(true);
            try { return source.Token; }
            catch (ObjectDisposedException) { return new CancellationToken(true); }
        }
    }

    /// <summary>
    /// 视频帧。
    /// </summary>
    private readonly PlaybackVideoFrameHub _videoFrames;

    /// <summary>
    /// 当前打开媒体实际使用的配置。
    /// </summary>
    private PlaybackOptions _currentOptions;

    /// <summary>
    /// 媒体打开和视频工作线程的综合诊断。
    /// </summary>
    private PlaybackDiagnosticInfo _diagnostic = new();

    /// <summary>
    /// 本次打开媒体后的关键生命周期时间点。
    /// </summary>
    private PlaybackLifecycleDiagnosticInfo _lifecycleDiagnostic = new();

    /// <summary>
    /// 当前播放是否处于暂停状态。
    /// </summary>
    private volatile bool _isPaused;

    /// <summary>
    /// 本次播放管线开始运行的时间。
    /// </summary>
    private DateTimeOffset? _playStartedAt;

    /// <summary>
    /// 从播放开始到界面首帧的耗时。
    /// </summary>
    private TimeSpan? _firstUiFrameElapsed;

    /// <summary>
    /// 最近一帧发布到显示端的时间。
    /// </summary>
    private DateTimeOffset? _lastUiFrameAt;

    /// <summary>
    /// 当前会话的媒体播放位置。
    /// </summary>
    private TimeSpan _currentTime;

    /// <summary>
    /// 重建解码线程时采用的媒体起点。
    /// </summary>
    private TimeSpan _workerStartPosition;

    /// <summary>
    /// 当前视频显示使用的播放速度。
    /// </summary>
    private double _playbackRate;

    /// <summary>
    /// 当前音量，范围为 0 到 1。
    /// </summary>
    private double _volume;

    /// <summary>
    /// 是否处于静音状态。
    /// </summary>
    private bool _isMuted;

    /// <summary>
    /// 跳转首帧显示后是否立即暂停。
    /// </summary>
    private bool _pauseAfterSeekFrame;

    /// <summary>
    /// 跳转结束后是否恢复播放。
    /// </summary>
    private bool _seekResumePlayingIntent;

    /// <summary>
    /// 根据当前帧输出和同步状态计算的健康诊断。
    /// </summary>
    private PlaybackHealthDiagnosticInfo _healthDiagnostic = new();

    /// <summary>
    /// 上次已记录的健康状态特征，避免重复日志。
    /// </summary>
    private string? _lastHealthSignature;

    /// <summary>
    /// 网络恢复尝试数量。
    /// </summary>
    private int _networkRecoveryAttemptCount;

    /// <summary>
    /// 最近网络错误。
    /// </summary>
    private string? _lastNetworkError;

    /// <summary>
    /// 当前跳转实际开始执行的时间。
    /// </summary>
    private PlaybackSeekFrame? _activeSeek;

    /// <summary>
    /// 当前实际执行中的跳转请求编号。
    /// </summary>
    private long ActiveSeekRequestId => _activeSeek?.RequestId ?? 0;

    /// <summary>
    /// 最近执行的播放控制操作名称。
    /// </summary>
    private string? _lastControlOperation;

    /// <summary>
    /// 最近一次播放控制操作的失败原因。
    /// </summary>
    private string? _lastControlError;

    /// <summary>
    /// 最近一次上报的音频输出诊断。
    /// </summary>
    private AudioPlaybackDiagnostics _audioDiagnostic = new();

    /// <summary>
    /// 显示端最新声明的 GPU 输出能力。
    /// </summary>
    private VideoOutputCapabilities _videoOutputCapabilities = new();

    /// <summary>
    /// 初始化播放会话所需的状态和资源。
    /// </summary>
    public PlaybackSession(PlaybackOptions? defaultOptions = null)
        : base(PlaybackState.Created, nameof(PlaybackSession))
    {
        _defaultOptions = defaultOptions ?? new PlaybackOptions();
        _currentOptions = _defaultOptions;
        _playbackRate = NormalizePlaybackRate(_defaultOptions.InitialPlaybackRate);
        _volume = Math.Clamp(_defaultOptions.InitialVolume, 0, 1);
        _isMuted = _defaultOptions.InitialMuted;
        _videoFrames = new PlaybackVideoFrameHub();
        _seeking = new PlaybackSeekController(InstanceId, ExecuteQueuedSeekAsync);
    }

    /// <summary>
    /// 检查播放状态是否允许切换。
    /// </summary>
    protected override bool IsTransitionAllowed(PlaybackState previousState, PlaybackState nextState)
    {
        if (nextState == PlaybackState.Disposed)
        {
            return previousState != PlaybackState.Disposed;
        }
        if (nextState == PlaybackState.Stopping)
        {
            return previousState is not PlaybackState.Created and not PlaybackState.Disposed;
        }

        return previousState switch
        {
            PlaybackState.Created => nextState == PlaybackState.Opening,
            PlaybackState.Opening => nextState is PlaybackState.Ready or PlaybackState.Failed,
            PlaybackState.Ready => nextState is PlaybackState.Playing or PlaybackState.Paused or PlaybackState.Seeking,
            PlaybackState.Playing => nextState is PlaybackState.Paused or PlaybackState.Seeking or PlaybackState.Completed or PlaybackState.Failed,
            PlaybackState.Paused => nextState is PlaybackState.Playing or PlaybackState.Seeking or PlaybackState.Completed or PlaybackState.Failed,
            PlaybackState.Seeking => nextState is PlaybackState.Playing or PlaybackState.Paused or PlaybackState.Completed or PlaybackState.Failed,
            PlaybackState.Stopping => nextState is PlaybackState.Stopped or PlaybackState.Failed,
            PlaybackState.Stopped => nextState is PlaybackState.Opening or PlaybackState.Playing or PlaybackState.Paused or PlaybackState.Seeking,
            PlaybackState.Completed => nextState is PlaybackState.Playing or PlaybackState.Paused or PlaybackState.Seeking or PlaybackState.Failed,
            PlaybackState.Failed => nextState == PlaybackState.Opening,
            PlaybackState.Disposed => false,
            _ => false
        };
    }

    /// <summary>
    /// 当前已打开的本地文件或网络媒体源。
    /// </summary>
    public MediaSource? CurrentSource { get; private set; }

    /// <summary>
    /// 当前媒体的容器和音视频流信息。
    /// </summary>
    public MediaInfo? CurrentMediaInfo { get; private set; }

    /// <summary>
    /// 当前处理到的媒体时间。
    /// </summary>
    public TimeSpan CurrentTime => _currentTime;

    /// <summary>
    /// 媒体总时长，未知时为空。
    /// </summary>
    public TimeSpan? Duration => CurrentMediaInfo?.Duration > TimeSpan.Zero ? CurrentMediaInfo.Duration : null;

    /// <summary>
    /// 网络源没有有限总时长时按直播处理。
    /// </summary>
    public bool IsLive
    {
        get
        {
            var source = CurrentSource;
            var info = CurrentMediaInfo;
            return source is not null && info is not null && PlaybackTimingPolicy.IsLiveNetworkSource(source, info.Duration);
        }
    }

    /// <summary>
    /// 由核心统一提供的跳转权限，跳转过程中仍可提交新目标。
    /// </summary>
    public bool CanSeek => !IsDisposed && State is not PlaybackState.Created and not PlaybackState.Opening
        and not PlaybackState.Stopping and not PlaybackState.Disposed and not PlaybackState.Failed && CanSeekCurrentSource();

    /// <summary>
    /// 播放速度。
    /// </summary>
    public double PlaybackRate => _playbackRate;

    /// <summary>
    /// 音量。
    /// </summary>
    public double Volume => _volume;

    /// <summary>
    /// 是否静音。
    /// </summary>
    public bool IsMuted => _isMuted;

    /// <summary>
    /// 组件运行状态变化时发出的通知。
    /// </summary>
    event EventHandler<PlaybackState>? IPlaybackSession.StateChanged
    {
        add => StateChanged += value;
        remove => StateChanged -= value;
    }

    /// <summary>
    /// 播放位置变化时发出的通知。
    /// </summary>
    public event EventHandler<PlaybackPositionChangedEventArgs>? PositionChanged;

    /// <summary>
    /// 新视频帧可供显示时发出的通知。
    /// </summary>
    public event EventHandler<PlaybackVideoFrameEventArgs>? VideoFrameReady;

    /// <summary>
    /// 更新显示端支持的 GPU 输出能力。
    /// </summary>
    public void ConfigureVideoOutput(VideoOutputCapabilities capabilities)
    {
        if (capabilities is null)
        {
            throw new ArgumentNullException(nameof(capabilities));
        }
        Volatile.Write(ref _videoOutputCapabilities, capabilities);
    }

    /// <summary>
    /// 打开新媒体，取消旧操作后串行替换整条管线。
    /// </summary>
    public async ValueTask OpenAsync(PlaybackOpenRequest request, CancellationToken cancellationToken = default)
    {
        var cancelVersion = Volatile.Read(ref _cancelVersion);
        ThrowIfDisposed();
        if (!IsSupportedPlaybackSource(request.Source) || request.Source.Kind == MediaSourceKind.File && !File.Exists(request.Source.Uri))
        {
            var error = CreateError(PlaybackError.OpenFailed, !IsSupportedPlaybackSource(request.Source) ? "媒体源不受支持。" : $"媒体文件不存在：{request.Source.Uri}");
            NotifyFault(error);
            throw new PlaybackException(PlaybackError.OpenFailed, error);
        }
        cancellationToken.ThrowIfCancellationRequested();
        _operationCts?.Cancel();
        await _pipelineMutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await StopCoreAsync(CancellationToken.None).ConfigureAwait(false);
            ResetOperationLifetime();
            if (cancelVersion != Volatile.Read(ref _cancelVersion)) _operationCts!.Cancel();
            await TransitionAsync(PlaybackState.Opening, cancellationToken).ConfigureAwait(false);
            CurrentSource = request.Source;
            CurrentMediaInfo = null;
            _currentOptions = request.Options ?? _defaultOptions;
            _lifecycleDiagnostic = new PlaybackLifecycleDiagnosticInfo();
            MarkLifecycle("OpenStarted");
            _diagnostic = new PlaybackDiagnosticInfo
            {
                SourceKind = request.Source.Kind,
                SourceUrl = request.Source.Uri,
                FFmpegOpenOptions = PlaybackFFmpegOptions.Describe(request.Source, _currentOptions)
            };
            _firstUiFrameElapsed = null;
            _lastUiFrameAt = null;
            _positionNotifications.Reset();
            _playStartedAt = null;
            _currentTime = TimeSpan.Zero;
            _workerStartPosition = TimeSpan.Zero;
            _pauseAfterSeekFrame = false;
            _seeking.Reset();
            _resyncSignal.Request(TimeSpan.Zero);
            _activeSeek = null;
            _lastControlOperation = "Open";
            _lastControlError = null;
            _audioDiagnostic = new AudioPlaybackDiagnostics { WorkerState = "未启动" };
            _networkRecoveryAttemptCount = 0;
            _lastNetworkError = null;
            _recording.Reset();
            _videoFrames.ResetCounters();
            _videoFrames.ResetPublicOutput();


            _retiredReadTimeouts = 0;
            _lastNetworkPacketAt = null;
            try
            {
                var timeout = request.Source.Kind == MediaSourceKind.NetworkStream ? PlaybackFFmpegOptions.EffectiveOpenTimeout(_currentOptions) : _currentOptions.OpenTimeout;
                using var open = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _operationCts!.Token);
                open.CancelAfter(timeout);
                var pipeline = CreatePipeline(TimeSpan.Zero);
                await pipeline.PrepareAsync(open.Token).ConfigureAwait(false);
                CurrentMediaInfo = pipeline.Demuxer.MediaInfo ?? throw new InvalidOperationException("未读取到媒体信息。");
                if (!CurrentMediaInfo.HasVideo) throw new InvalidOperationException("当前媒体没有视频流。");
                if (pipeline.Demuxer.OpenDiagnostic is not null) UpdateOpenDiagnostics(pipeline.Demuxer.OpenDiagnostic);
                await TransitionAsync(PlaybackState.Ready, cancellationToken).ConfigureAwait(false);
                MarkLifecycle("OpenCompleted");
                FluxMediaLog.Write(LogLevel.Information, ComponentName, InstanceId,
                    $"Event=MediaSeekPermission IsLive={IsLive} CanSeek={CanSeek} Duration={Duration} SourceKind={CurrentSource?.Kind}");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || _operationCts?.IsCancellationRequested != false)
            {
                await StopCoreAsync(CancellationToken.None).ConfigureAwait(false);
                throw;
            }
            catch (Exception exception)
            {
                await RetirePipelineAsync().ConfigureAwait(false);
                await TransitionAsync(PlaybackState.Failed).ConfigureAwait(false);
                var error = CreateError(PlaybackError.OpenFailed, exception is OperationCanceledException ? "打开媒体超时。" : $"打开媒体失败：{exception.Message}");
                NotifyFault(error, exception);
                throw new PlaybackException(PlaybackError.OpenFailed, error, exception);
            }
        }
        finally { _pipelineMutationGate.Release(); }
    }

    /// <summary>
    /// 启动解码和音频输出，结束后可重新播放。
    /// </summary>
    public async ValueTask<PlaybackResult> PlayAsync(CancellationToken cancellationToken = default)
    {
        var cancelVersion = Volatile.Read(ref _cancelVersion);
        await _pipelineMutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (cancelVersion != Volatile.Read(ref _cancelVersion)) throw new OperationCanceledException();
            return await PlayCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { _pipelineMutationGate.Release(); }
    }

    /// <summary>
    /// 在管线锁内启动、重播或恢复，所有重建使用同一条路径。
    /// </summary>
    private async ValueTask<PlaybackResult> PlayCoreAsync(CancellationToken cancellationToken)
    {
        EnsureOpened();
        cancellationToken.ThrowIfCancellationRequested();
        if (State == PlaybackState.Failed) return ControlFailure(PlaybackError.InvalidState, "播放已失败，请重新打开媒体。");
        if (State == PlaybackState.Playing && _pipeline is { IsCanceled: false, Completion.IsCompleted: false }) return PlaybackResult.Success();
        if (_operationCts is null || _operationCts.IsCancellationRequested) ResetOperationLifetime();
        _isPaused = false;
        _pauseAfterSeekFrame = false;
        MarkLifecycle("PlayRequested");
        if (_pipeline is null || _pipeline.IsCanceled || _pipeline.Completion?.IsCompleted == true)
        {
            var position = State == PlaybackState.Completed ? TimeSpan.Zero : _workerStartPosition;
            await RestartPlaybackWorkerCoreAsync(position, true, cancellationToken).ConfigureAwait(false);
        }
        else if (_pipeline.Completion is null)
        {
            _recording.Attach(_pipeline.Demuxer);
            await TransitionAsync(PlaybackState.Playing, cancellationToken).ConfigureAwait(false);
            StartPipeline(_pipeline);
        }
        else await TransitionAsync(PlaybackState.Playing, cancellationToken).ConfigureAwait(false);
        MarkLifecycle("PlayStarted");
        return PlaybackResult.Success();
    }

    /// <summary>
    /// 暂停播放并保留当前位置。
    /// </summary>
    public async ValueTask<PlaybackResult> PauseAsync(CancellationToken cancellationToken = default)
    {
        await _pipelineMutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await PauseCoreAsync(cancellationToken).ConfigureAwait(false); }
        finally { _pipelineMutationGate.Release(); }
    }

    /// <summary>
    /// 在管线锁内暂停，避免恢复过程覆盖用户意图。
    /// </summary>
    private async ValueTask<PlaybackResult> PauseCoreAsync(CancellationToken cancellationToken)
    {
        EnsureOpened();
        if (State == PlaybackState.Paused)
        {
            return PlaybackResult.Success();
        }

        cancellationToken.ThrowIfCancellationRequested();
        _isPaused = true;
        MarkLifecycle("PauseRequested");
        FluxMediaLog.Write(LogLevel.Information, ComponentName, InstanceId, $"Pause。State={State}, SourceKind={CurrentSource?.Kind}, Source={CurrentSource?.Uri}");
        FluxMediaLog.Write(LogLevel.Information, ComponentName, InstanceId, $"Event=PauseAudio AudioState={_audioDiagnostic.WorkerState} AudioTime={_audioDiagnostic.CurrentTime?.ToString() ?? "-"} DeviceState={_audioDiagnostic.OutputDeviceState ?? "-"}");
        await TransitionAsync(PlaybackState.Paused, cancellationToken).ConfigureAwait(false);
        MarkLifecycle("Paused");
        return PlaybackResult.Success();
    }

    /// <summary>
    /// 恢复暂停的播放。
    /// </summary>
    public ValueTask<PlaybackResult> ResumeAsync(CancellationToken cancellationToken = default)
    {
        FluxMediaLog.Write(LogLevel.Information, ComponentName, InstanceId, $"Resume。State={State}, SourceKind={CurrentSource?.Kind}, Source={CurrentSource?.Uri}");
        FluxMediaLog.Write(LogLevel.Information, ComponentName, InstanceId, $"Event=ResumeAudio AudioState={_audioDiagnostic.WorkerState} AudioTime={_audioDiagnostic.CurrentTime?.ToString() ?? "-"} DeviceState={_audioDiagnostic.OutputDeviceState ?? "-"}");
        return PlayAsync(cancellationToken);
    }

    /// <summary>
    /// 跳转到指定媒体时间。
    /// </summary>
    public async ValueTask<PlaybackResult> SeekAsync(TimeSpan position, CancellationToken cancellationToken = default)
    {
        EnsureOpened();
        cancellationToken.ThrowIfCancellationRequested();
        if (!CanSeek)
        {
            FluxMediaLog.Write(LogLevel.Warning, ComponentName, InstanceId,
                $"Event=SeekRejected IsLive={IsLive} Duration={Duration} State={State} Requested={position}");
            return PlaybackResult.Failure(PlaybackError.NotSeekable, IsLive ? "直播流不允许调整播放进度。" : "当前媒体尚不支持时间跳转。");
        }

        var target = ClampPosition(position);
        if (State != PlaybackState.Seeking)
        {
            _seekResumePlayingIntent = State == PlaybackState.Playing;
        }

        var resumePlaying = _seekResumePlayingIntent;
        var completion = _seeking.Enqueue(target, resumePlaying, OperationToken);
        return await WaitForSeekCompletionAsync(completion, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 执行跳转并等待首帧恢复。
    /// </summary>
    private async Task<PlaybackResult> ExecuteSeekAsync(TimeSpan target, bool resumePlaying, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!CanSeek)
        {
            FluxMediaLog.Write(LogLevel.Warning, ComponentName, InstanceId,
                $"Event=SeekExecutionRejected IsLive={IsLive} Duration={Duration} State={State} Requested={target}");
            return PlaybackResult.Failure(PlaybackError.NotSeekable, "当前媒体不允许调整播放进度。");
        }
        var startedAt = DateTimeOffset.UtcNow;
        _resyncSignal.InvalidateFrames();
        _videoFrames.ClearPublicOutput();
        _activeSeek = new PlaybackSeekFrame(_seeking.Diagnostic.RequestId, target, startedAt);
        _seeking.Diagnostic = _seeking.Diagnostic with
        {
            RequestedPosition = target,
            ActualPosition = null,
            Path = null,
            InProgress = true,
            DemuxerSeekElapsed = null,
            TotalElapsed = null,
            FirstUiFrameElapsed = null,
            FirstUiRecoveryAttempted = false,
            RecoveryReason = null,
            StartedAt = startedAt,
            CompletedAt = null,
            ResyncVersion = null,
            LastError = null
        };
        _seeking.Trace(ActiveSeekRequestId, "Start", $"Target={target} ResumePlaying={resumePlaying}", target);
        FluxMediaLog.Write(LogLevel.Information, ComponentName, InstanceId, $"Event=Seek Start={_currentTime} Target={target} ResumePlaying={resumePlaying} SourceKind={CurrentSource?.Kind} Source={CurrentSource?.Uri}");
        _lastControlOperation = "Seek";
        _lastControlError = null;
        await TransitionAsync(PlaybackState.Seeking, cancellationToken).ConfigureAwait(false);
        var recordingSuspended = _recording.IsRecording && Demuxer is not null;
        if (recordingSuspended)
        {
            Demuxer!.SuspendRecordingForDiscontinuity();
            FluxMediaLog.Write(
                LogLevel.Information, ComponentName, InstanceId, $"Event=PlaybackRecordingSeekSuspended Target={target}");
        }
        try
        {
            if (IsAtDurationEnd(target))
            {
                ApplyTerminalSeek(target, resumePlaying, startedAt);
                Demuxer?.ResumeRecordingAfterDiscontinuity();
                recordingSuspended = false;
                await TransitionAsync(resumePlaying ? PlaybackState.Completed : PlaybackState.Paused, cancellationToken).ConfigureAwait(false);
                if (resumePlaying)
                {
                    MarkLifecycle("Completed");
                }

                return PlaybackResult.Success();
            }

            if (!await TryDemuxerSeekAsync(target, resumePlaying, cancellationToken).ConfigureAwait(false))
            {
                await RestartPlaybackWorkerCoreAsync(target, resumePlaying, cancellationToken).ConfigureAwait(false);
                MarkSeekPath("FullRestart", target, null);
                _seeking.Trace(ActiveSeekRequestId, "FullRestart", $"Completed Target={target}", target);
            }

            Demuxer?.ResumeRecordingAfterDiscontinuity();
            recordingSuspended = false;
            if (_recording.IsRecording)
            {
                FluxMediaLog.Write(
                    LogLevel.Information, ComponentName, InstanceId, $"Event=PlaybackRecordingSeekResumed Target={target} Path={_seeking.Diagnostic.Path ?? "-"}");
            }

            if (!await WaitForSeekFirstUiOrRecoverAsync(target, resumePlaying, startedAt, cancellationToken).ConfigureAwait(false))
            {
                InvokeSubscribersSafely(PositionChanged, new PlaybackPositionChangedEventArgs(target, Duration), nameof(PositionChanged));
                await RestorePostSeekStateAsync(resumePlaying).ConfigureAwait(false);
                _seeking.Diagnostic = _seeking.Diagnostic with
                {
                    InProgress = false,
                    CompletedAt = DateTimeOffset.UtcNow,
                    TotalElapsed = DateTimeOffset.UtcNow - startedAt,
                    LastError = "Seek completed without a recovered UI frame."
                };
                _seeking.UpdateStrategy(_seeking.Diagnostic);
                _seeking.Trace(ActiveSeekRequestId, "RecoveryFailed", "Seek completed without a recovered UI frame.", target, _seeking.Diagnostic.TotalElapsed);
                return PlaybackResult.Failure(PlaybackError.PlayFailed, "Seek 后未能恢复视频首帧。");
            }

            InvokeSubscribersSafely(PositionChanged, new PlaybackPositionChangedEventArgs(target, Duration), nameof(PositionChanged));
            await RestorePostSeekStateAsync(resumePlaying).ConfigureAwait(false);
            _seeking.Diagnostic = _seeking.Diagnostic with
            {
                InProgress = false,
                CompletedAt = DateTimeOffset.UtcNow,
                TotalElapsed = DateTimeOffset.UtcNow - startedAt
            };
            _seeking.UpdateStrategy(_seeking.Diagnostic);
            _seeking.Trace(ActiveSeekRequestId, "Completed", $"Path={_seeking.Diagnostic.Path ?? "-"} Total={_seeking.Diagnostic.TotalElapsed?.ToString() ?? "-"}", _seeking.Diagnostic.ActualPosition, _seeking.Diagnostic.TotalElapsed);
            return PlaybackResult.Success();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _seeking.Diagnostic = _seeking.Diagnostic with
            {
                InProgress = false,
                CompletedAt = DateTimeOffset.UtcNow,
                TotalElapsed = DateTimeOffset.UtcNow - startedAt,
                LastError = "Seek canceled."
            };
            _seeking.Trace(ActiveSeekRequestId, "Canceled", "Seek canceled by the playback lifecycle.", target, _seeking.Diagnostic.TotalElapsed);
            throw;
        }
        catch (Exception ex)
        {
            if (_pipeline?.IsCanceled == false && _resyncSignal.Snapshot().FrameEpoch?.IsCurrent == false)
                _resyncSignal.Request(_currentTime);
            await RestorePostSeekStateAsync(resumePlaying).ConfigureAwait(false);
            _seeking.Diagnostic = _seeking.Diagnostic with
            {
                InProgress = false,
                CompletedAt = DateTimeOffset.UtcNow,
                TotalElapsed = DateTimeOffset.UtcNow - startedAt,
                LastError = ex.Message
            };
            _seeking.Trace(ActiveSeekRequestId, "Failed", ex.Message, target, _seeking.Diagnostic.TotalElapsed);
            throw;
        }
        finally
        {
            if (recordingSuspended)
            {
                Demuxer?.ResumeRecordingAfterDiscontinuity();
            }
            _activeSeek = null;
        }
    }

    /// <summary>
    /// 判断目标位置是否已到媒体末尾。
    /// </summary>
    private bool IsAtDurationEnd(TimeSpan target)
    {
        return Duration.HasValue && target >= Duration.Value - TimeSpan.FromMilliseconds(1);
    }

    /// <summary>
    /// 处理跳转到媒体末尾时的位置和暂停状态。
    /// </summary>
    private void ApplyTerminalSeek(TimeSpan target, bool resumePlaying, DateTimeOffset startedAt)
    {
        _pipeline?.Cancel();
        _isPaused = !resumePlaying;
        _pauseAfterSeekFrame = false;
        _currentTime = target;
        InvokeSubscribersSafely(PositionChanged, new PlaybackPositionChangedEventArgs(target, Duration), nameof(PositionChanged));
        _seeking.Diagnostic = _seeking.Diagnostic with
        {
            ActualPosition = target,
            Path = "EndOfMedia",
            InProgress = false,
            CompletedAt = DateTimeOffset.UtcNow,
            TotalElapsed = DateTimeOffset.UtcNow - startedAt,
            FirstUiFrameElapsed = TimeSpan.Zero,
            LastError = null
        };
        _seeking.UpdateStrategy(_seeking.Diagnostic);
        _seeking.Trace(ActiveSeekRequestId, "Completed", $"Path=EndOfMedia ResumePlaying={resumePlaying}", target, _seeking.Diagnostic.TotalElapsed);
    }

    /// <summary>
    /// 按原始播放意图恢复跳转后的状态。
    /// </summary>
    private async Task RestorePostSeekStateAsync(bool resumePlaying)
    {
        if (State is PlaybackState.Stopping or PlaybackState.Stopped or PlaybackState.Disposed ||
            _operationCts?.IsCancellationRequested == true)
        {
            return;
        }

        _isPaused = !resumePlaying;
        _pauseAfterSeekFrame = false;
        _seekResumePlayingIntent = resumePlaying;
        if (State == PlaybackState.Seeking)
        {
            await TransitionAsync(
                resumePlaying ? PlaybackState.Playing : PlaybackState.Paused, CancellationToken.None).ConfigureAwait(false);
        }

        FluxMediaLog.Write(
            LogLevel.Information, ComponentName, InstanceId, $"Event=SeekStateRestored State={State} ResumePlaying={resumePlaying} IsPaused={_isPaused} Position={_currentTime}");
    }

    /// <summary>
    /// 等待跳转完成，调用方取消时结束等待。
    /// </summary>
    private static async Task<PlaybackResult> WaitForSeekCompletionAsync(Task<PlaybackResult> completion, CancellationToken cancellationToken)
    {
        if (!cancellationToken.CanBeCanceled)
        {
            return await completion.ConfigureAwait(false);
        }

        var cancellation = new TaskCompletionSource<PlaybackResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = cancellationToken.Register(static state =>
        {
            var source = (TaskCompletionSource<PlaybackResult>)state!;
            source.TrySetCanceled();
        }, cancellation);

        var completed = await Task.WhenAny(completion, cancellation.Task).ConfigureAwait(false);
        return await completed.ConfigureAwait(false);
    }

    /// <summary>
    /// 设置播放速度。
    /// </summary>
    public ValueTask<PlaybackResult> SetPlaybackRateAsync(double rate, CancellationToken cancellationToken = default)
    {
        EnsureOpened();
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsSupportedRate(rate))
        {
            return new ValueTask<PlaybackResult>(ControlFailure(PlaybackError.PlaybackRateNotSupported, "当前只支持 0.5x、1.0x、1.5x、2.0x。"));
        }

        if (!CanUsePresentationControls())
        {
            return new ValueTask<PlaybackResult>(ControlFailure(PlaybackError.PlaybackRateNotSupported, "实时流当前不支持倍速播放。"));
        }

        _playbackRate = rate;
        _lastControlOperation = "SetPlaybackRate";
        _lastControlError = null;
        FluxMediaLog.Write(LogLevel.Information, ComponentName, InstanceId, $"Event=SetPlaybackRate Rate={rate:0.###} State={State} SourceKind={CurrentSource?.Kind} Source={CurrentSource?.Uri}");
        return new ValueTask<PlaybackResult>(PlaybackResult.Success());
    }

    /// <summary>
    /// 设置音量，范围为 0 到 1。
    /// </summary>
    public ValueTask<PlaybackResult> SetVolumeAsync(double volume, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (volume is < 0 or > 1)
        {
            var error = CreateError(PlaybackError.InvalidState, "音量必须在 0 到 1 之间。");
            NotifyFault(error);
            throw new PlaybackException(PlaybackError.InvalidState, error);
        }

        _volume = volume;
        _pipeline?.SetVolume(_volume, _isMuted);
        _lastControlOperation = "SetVolume";
        _lastControlError = null;
        FluxMediaLog.Write(LogLevel.Information, ComponentName, InstanceId, $"Event=SetVolume Volume={volume:0.###} Muted={_isMuted} AudioOutputAvailable={_pipeline?.AudioOutputAvailable == true} Backend={_pipeline?.AudioBackend ?? "-"}");
        return new ValueTask<PlaybackResult>(PlaybackResult.Success());
    }

    /// <summary>
    /// 设置静音状态。
    /// </summary>
    public ValueTask<PlaybackResult> SetMutedAsync(bool muted, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _isMuted = muted;
        _pipeline?.SetVolume(_volume, _isMuted);
        _lastControlOperation = "SetMuted";
        _lastControlError = null;
        FluxMediaLog.Write(LogLevel.Information, ComponentName, InstanceId, $"Event=SetMuted Muted={muted} Volume={_volume:0.###} AudioOutputAvailable={_pipeline?.AudioOutputAvailable == true} Backend={_pipeline?.AudioBackend ?? "-"}");
        return new ValueTask<PlaybackResult>(PlaybackResult.Success());
    }

    /// <summary>
    /// 在统一管线锁内启动或预约录制。
    /// </summary>
    public async ValueTask<PlaybackResult> StartRecordingAsync(PlaybackRecordingOptions options, CancellationToken cancellationToken = default)
    {
        EnsureOpened();
        await _pipelineMutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (CurrentMediaInfo?.HasVideo != true) return ControlFailure(PlaybackError.RecordingFailed, "当前媒体没有视频流。");
            if (State is PlaybackState.Created or PlaybackState.Opening or PlaybackState.Failed or PlaybackState.Stopping or PlaybackState.Disposed)
                return ControlFailure(PlaybackError.InvalidState, "当前状态无法开始录制。");
            var result = _recording.Start(options, Demuxer, CurrentMediaInfo.HasAudio);
            _lastControlOperation = "StartRecording";
            _lastControlError = result.IsSuccess ? null : result.Message;
            return result;
        }
        finally { _pipelineMutationGate.Release(); }
    }

    /// <summary>
    /// 在统一管线锁内停止录制并封装文件。
    /// </summary>
    public async ValueTask<PlaybackResult> StopRecordingAsync(CancellationToken cancellationToken = default)
    {
        await _pipelineMutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await StopRecordingCoreAsync(cancellationToken).ConfigureAwait(false); }
        finally { _pipelineMutationGate.Release(); }
    }

    /// <summary>
    /// 返回活动或最近一次录制的诊断快照。
    /// </summary>
    public PlaybackRecordingDiagnosticInfo GetRecordingDiagnosticInfo() => _recording.Snapshot(Demuxer);

    /// <summary>
    /// 获取已打开媒体的基本信息。
    /// </summary>
    public ValueTask<MediaInfo> GetMediaInfoAsync(CancellationToken cancellationToken = default)
    {
        EnsureOpened();
        cancellationToken.ThrowIfCancellationRequested();
        return new ValueTask<MediaInfo>(CurrentMediaInfo!);
    }

    /// <summary>
    /// 获取当前运行状态和诊断信息。
    /// </summary>
    public PlaybackDiagnosticInfo GetDiagnosticInfo()
    {
        var hasDuration = CurrentMediaInfo?.Duration > TimeSpan.Zero;
        TimeSpan? audioVideoDrift = _audioDiagnostic.CurrentTime.HasValue ? _audioDiagnostic.CurrentTime.Value - _currentTime : null;
        var diagnostic = _diagnostic with
        {
            SourceKind = CurrentSource?.Kind ?? _diagnostic.SourceKind,
            SourceUrl = CurrentSource?.Uri ?? _diagnostic.SourceUrl,
            Protocol = _diagnostic.Protocol ?? ProtocolOf(CurrentSource),
            Container = CurrentMediaInfo?.ContainerFormat ?? _diagnostic.Container,
            FrameRate = CurrentMediaInfo?.VideoFrameRate ?? _diagnostic.FrameRate,
            TimingMode = _diagnostic.TimingMode,
            LowLatencyEnabled = _diagnostic.TimingMode == PlaybackTimingMode.RealtimeLowLatency,
            FirstUiFrameElapsed = _firstUiFrameElapsed ?? _diagnostic.FirstUiFrameElapsed,
            LastUiFrameAt = _lastUiFrameAt ?? _diagnostic.LastUiFrameAt,
            DroppedFrameCount = _videoFrames.DroppedFrameCount,
            VideoFrameReadyCount = _videoFrames.VideoFrameReadyCount,
            WorkerRunning = _pipeline?.Completion is { IsCompleted: false } && _diagnostic.WorkerRunning,
            CanSeek = CanSeek,
            IsLive = IsLive,
            HasDuration = hasDuration,
            CurrentTime = _currentTime,
            Duration = Duration,
            PlaybackRate = _playbackRate,
            Volume = _volume,
            IsMuted = _isMuted,
            AudioOutputAvailable = _pipeline?.AudioOutputAvailable == true,
            AudioStreamIndex = _audioDiagnostic.StreamIndex,
            AudioSampleRate = _audioDiagnostic.SampleRate,
            AudioChannels = _audioDiagnostic.Channels,
            AudioOutputFormat = _audioDiagnostic.OutputFormat,
            AudioWorkerState = _audioDiagnostic.WorkerState,
            AudioDecodedFrameCount = _audioDiagnostic.DecodedFrameCount,
            AudioWrittenPcmBytes = _audioDiagnostic.WrittenPcmBytes,
            AudioCurrentTime = _audioDiagnostic.CurrentTime,
            VideoCurrentTime = _currentTime,
            AudioVideoDrift = audioVideoDrift,
            LastAudioError = _audioDiagnostic.LastError,
            AudioOutputDeviceState = _audioDiagnostic.OutputDeviceState,
            LastControlOperation = _lastControlOperation,
            LastControlError = _lastControlError,
            Lifecycle = _lifecycleDiagnostic,
            Health = BuildHealthDiagnostic(audioVideoDrift),
            Network = BuildNetworkDiagnostic(),
            LastSeek = _seeking.Diagnostic with
            {
                Trace = _seeking.Snapshot()
            }
        };
        return diagnostic;
    }

    /// <summary>
    /// 取消待执行操作并停止播放管线。
    /// </summary>
    public async ValueTask StopAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CancelPendingOperations();
        await _pipelineMutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await StopCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _pipelineMutationGate.Release();
        }
    }

    /// <summary>
    /// 不等待命令锁，立即取消打开、跳转和重连。
    /// </summary>
    public void CancelPendingOperations()
    {
        Interlocked.Increment(ref _cancelVersion);
        try { _operationCts?.Cancel(); }
        catch (ObjectDisposedException) { }
        _seeking.CancelPending(new OperationCanceledException("播放操作已取消。"));
    }

    /// <summary>
    /// 先停止录制，再等待管线归还全部资源。
    /// </summary>
    private async ValueTask StopCoreAsync(CancellationToken cancellationToken)
    {
        if (IsDisposed || State == PlaybackState.Disposed) return;
        if (State is PlaybackState.Created or PlaybackState.Stopped)
        {
            await StopRecordingCoreAsync(CancellationToken.None).ConfigureAwait(false);
            await RetirePipelineAsync().ConfigureAwait(false);
            return;
        }
        await TransitionAsync(PlaybackState.Stopping, cancellationToken).ConfigureAwait(false);
        MarkLifecycle("StopStarted");
        _seeking.CancelPending(new OperationCanceledException("Playback has been stopped."));
        await StopRecordingCoreAsync(CancellationToken.None).ConfigureAwait(false);
        _isPaused = false;
        _pauseAfterSeekFrame = false;
        await RetirePipelineAsync().ConfigureAwait(false);
        _videoFrames.ResetPublicOutput();
        await TransitionAsync(PlaybackState.Stopped).ConfigureAwait(false);
        MarkLifecycle("StopCompleted");
    }

    /// <summary>
    /// 依次读取视频帧；跨迭代使用时需保留租约。
    /// </summary>
    public async IAsyncEnumerable<VideoFrame> ReadVideoFramesAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        EnsureOpened();
        await foreach (var frame in _videoFrames.ReadVideoFramesAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return frame;
        }
    }

    /// <summary>
    /// 停止唯一管线并释放会话级状态。
    /// </summary>
    protected override async ValueTask DisposeCoreAsync()
    {
        await StopAsync().ConfigureAwait(false);
        await _seeking.DisposeAsync().ConfigureAwait(false);
        _operationCts?.Dispose();
        _videoFrames.ResetPublicOutput();
        await TransitionAsync(PlaybackState.Disposed).ConfigureAwait(false);
        FluxMediaLog.Flush();
    }

    /// <summary>
    /// 发布视频帧并更新进度与首帧统计。
    /// </summary>
    private ValueTask PublishVideoFrameAsync(VideoFrame frame, long _)
    {
        if (!frame.IsCurrent) { _videoFrames.RecordDrop(); return default; }
        var seek = _activeSeek;
        _currentTime = frame.Timestamp;
        var now = DateTimeOffset.UtcNow;
        _lastUiFrameAt = now;
        if (_positionNotifications.ShouldPublish(now))
        {
            InvokeSubscribersSafely(PositionChanged, new PlaybackPositionChangedEventArgs(_currentTime, Duration), nameof(PositionChanged));
        }
        InvokeSubscribersSafely(VideoFrameReady, new PlaybackVideoFrameEventArgs(frame), nameof(VideoFrameReady));
        if (_lifecycleDiagnostic.FirstUiFrameAt is null)
        {
            MarkLifecycle("FirstUiFrame");
        }
        if (seek is not null && seek.TryAccept(frame, out var elapsed))
        {
            _seeking.Diagnostic = _seeking.Diagnostic with { FirstUiFrameElapsed = elapsed };
            _seeking.Trace(seek.RequestId, "FirstUI", $"Frame={frame.Timestamp} Timeline={frame.TimelineId} Elapsed={elapsed}", frame.Timestamp, elapsed);
        }

        if (_pauseAfterSeekFrame && (seek is null || seek.FirstFrame.IsCompletedSuccessfully))
        {
            _pauseAfterSeekFrame = false;
            _isPaused = true;
        }

        _firstUiFrameElapsed ??= _playStartedAt is null ? null : DateTimeOffset.UtcNow - _playStartedAt.Value;
        return default;
    }

    /// <summary>
    /// 录制器由会话接管，管线重建失败后仍可继续下一次恢复。
    /// </summary>
    private async Task RestartPlaybackWorkerCoreAsync(TimeSpan startPosition, bool resumePlaying, CancellationToken cancellationToken)
    {
        _recording.Detach(Demuxer);
        await RetirePipelineAsync().ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var synchronization = _resyncSignal.Request(startPosition);
        if (synchronization.FrameEpoch is { } epoch) _activeSeek?.BindEpoch(epoch);
        _workerStartPosition = startPosition;
        _currentTime = startPosition;
        _isPaused = false;
        _pauseAfterSeekFrame = !resumePlaying;
        _firstUiFrameElapsed = null;
        _playStartedAt = DateTimeOffset.UtcNow;
        try
        {
            var pipeline = CreatePipeline(startPosition);
            await pipeline.PrepareAsync(cancellationToken).ConfigureAwait(false);
            _recording.Attach(pipeline.Demuxer);
            if (State is PlaybackState.Ready or PlaybackState.Stopped or PlaybackState.Completed)
                await TransitionAsync(resumePlaying ? PlaybackState.Playing : PlaybackState.Paused, cancellationToken).ConfigureAwait(false);
            StartPipeline(pipeline);
        }
        catch
        {
            await RetirePipelineAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// 尝试让现有解复用器定位到目标时间。
    /// </summary>
    private async Task<bool> TryDemuxerSeekAsync(TimeSpan target, bool resumePlaying, CancellationToken cancellationToken)
    {
        var demuxer = Demuxer;
        if (demuxer is null || _pipeline?.IsCanceled != false)
        {
            return false;
        }

        try
        {
            FluxMediaLog.Write(LogLevel.Information, ComponentName, InstanceId, $"Event=SeekDemuxerStart Target={target} ResumePlaying={resumePlaying} Current={_currentTime}");
            _seeking.Trace(ActiveSeekRequestId, "DemuxerStart", $"Target={target} Current={_currentTime}", target);
            _isPaused = true;
            _pipeline?.ClearAudio();
            _videoFrames.ClearPublicOutput();
            var demuxStartedAt = DateTimeOffset.UtcNow;
            var actualStart = await demuxer.SeekAsync(target, cancellationToken).ConfigureAwait(false);
            var demuxElapsed = DateTimeOffset.UtcNow - demuxStartedAt;
            ApplyActiveDecoderResync(actualStart, resumePlaying, "DemuxerSeek");
            MarkSeekPath("Demuxer", actualStart, demuxElapsed);
            _seeking.Trace(ActiveSeekRequestId, "DemuxerCompleted", $"Target={target} Actual={actualStart}", actualStart, demuxElapsed);
            FluxMediaLog.Write(LogLevel.Information, ComponentName, InstanceId, $"Event=SeekDemuxerCompleted Target={target} ActualStart={actualStart} ResumePlaying={resumePlaying}");
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            FluxMediaLog.Write(LogLevel.Warning, ComponentName, InstanceId, $"Event=SeekDemuxerFailed Target={target} Error={ex.Message}", ex);
            _seeking.Trace(ActiveSeekRequestId, "DemuxerFailed", ex.Message, target);
            return false;
        }
    }

    /// <summary>
    /// 通知音视频解码器清空旧状态并重新同步。
    /// </summary>
    private void ApplyActiveDecoderResync(TimeSpan position, bool resumePlaying, string reason)
    {
        _pipeline?.ClearAudio();
        _videoFrames.ClearPublicOutput();
        var request = _resyncSignal.Request(position);
        if (request.FrameEpoch is { } epoch) _activeSeek?.BindEpoch(epoch);
        _workerStartPosition = position;
        _currentTime = position;
        _isPaused = false;
        _pauseAfterSeekFrame = !resumePlaying;
        _firstUiFrameElapsed = null;
        _playStartedAt = DateTimeOffset.UtcNow;
        _audioDiagnostic = _audioDiagnostic with
        {
            WorkerState = reason,
            CurrentTime = position,
            OutputBufferedDuration = TimeSpan.Zero,
            OutputDeviceDelay = TimeSpan.Zero
        };
        _seeking.Diagnostic = _seeking.Diagnostic with
        {
            ResyncVersion = request.Version
        };
        _seeking.Trace(ActiveSeekRequestId, "DecoderResync", $"Reason={reason} Version={request.Version} Position={position}", position);
        FluxMediaLog.Write(LogLevel.Information, ComponentName, InstanceId, $"Event=DecoderResyncSignal Reason={reason} Version={request.Version} Timeline={request.FrameEpoch?.Id} Position={position} ResumePlaying={resumePlaying}");
    }

    /// <summary>
    /// 记录本次跳转实际采用的执行路径。
    /// </summary>
    private void MarkSeekPath(string path, TimeSpan actualPosition, TimeSpan? demuxerSeekElapsed)
    {
        _seeking.Diagnostic = _seeking.Diagnostic with
        {
            Path = path,
            ActualPosition = actualPosition,
            Strategy = _seeking.Strategy,
            NextCoalesceDelay = _seeking.CoalesceDelay,
            DemuxerSeekElapsed = demuxerSeekElapsed
        };
    }

    /// <summary>
    /// 等待跳转首帧，超时时重建管线恢复。
    /// </summary>
    private async Task<bool> WaitForSeekFirstUiOrRecoverAsync(TimeSpan target, bool resumePlaying, DateTimeOffset seekStartedAt, CancellationToken cancellationToken)
    {
        if (CurrentMediaInfo?.HasVideo != true)
        {
            return true;
        }

        if (await WaitForSeekFirstUiAsync(FirstUiRecoveryTimeout, cancellationToken).ConfigureAwait(false))
        {
            return true;
        }

        _seeking.Trace(ActiveSeekRequestId, "FirstUITimeout", $"Timeout={FirstUiRecoveryTimeout} Path={_seeking.Diagnostic.Path ?? "-"}", target, DateTimeOffset.UtcNow - seekStartedAt);
        if (string.Equals(_seeking.Diagnostic.Path, "FullRestart", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(_seeking.Diagnostic.Path, "FullRestartRecovery", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        _seeking.Diagnostic = _seeking.Diagnostic with
        {
            FirstUiRecoveryAttempted = true,
            RecoveryReason = "FirstUiTimeout"
        };
        _seeking.Trace(ActiveSeekRequestId, "RecoveryFullRestart", $"Target={target} PreviousPath={_seeking.Diagnostic.Path ?? "-"}", target, DateTimeOffset.UtcNow - seekStartedAt);
        await RestartPlaybackWorkerCoreAsync(target, resumePlaying, cancellationToken).ConfigureAwait(false);
        MarkSeekPath("FullRestartRecovery", target, null);
        return await WaitForSeekFirstUiAsync(FirstUiRecoveryTimeout, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 等待跳转后的视频首帧到达显示端。
    /// </summary>
    private async Task<bool> WaitForSeekFirstUiAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var firstFrame = _activeSeek?.FirstFrame;
        if (firstFrame is null) return false;
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var deadline = Task.Delay(timeout, wait.Token);
        var completed = await Task.WhenAny(firstFrame, deadline).ConfigureAwait(false);
        if (completed == firstFrame)
        {
            wait.Cancel();
            await firstFrame.ConfigureAwait(false);
            return true;
        }
        cancellationToken.ThrowIfCancellationRequested();
        return false;
    }

    /// <summary>
    /// 重建失效的网络播放管线。
    /// </summary>
    private async Task TryRecoverNetworkPlaybackAsync(PlaybackPipeline failedPipeline, string reason)
    {
        var operation = _operationCts;
        if (operation is null || operation.IsCancellationRequested) return;
        CancellationToken operationToken;
        try { operationToken = operation.Token; }
        catch (ObjectDisposedException) { return; }
        var gateEntered = false;
        try
        {
            await _pipelineMutationGate.WaitAsync(operationToken).ConfigureAwait(false);
            gateEntered = true;
            // 获取锁后重新校验，防止旧管线重复恢复或覆盖新媒体。
            if (!ReferenceEquals(_pipeline, failedPipeline) || !ReferenceEquals(_operationCts, operation) ||
                IsDisposed || State is PlaybackState.Stopping or PlaybackState.Stopped or PlaybackState.Disposed or PlaybackState.Failed)
                return;

            while (_currentOptions.Network.ReconnectEnabled &&
                _networkRecoveryAttemptCount < Math.Max(0, _currentOptions.NetworkReconnectAttempts))
            {
                operationToken.ThrowIfCancellationRequested();
                var attempt = Interlocked.Increment(ref _networkRecoveryAttemptCount);
                var resumePlaying = State != PlaybackState.Paused && !_isPaused;
                var startPosition = CanSeekCurrentSource() ? _currentTime : TimeSpan.Zero;
                FluxMediaLog.Write(LogLevel.Warning, ComponentName, InstanceId, $"Event=NetworkRecoveryStart Attempt={attempt} Reason={reason} ResumePlaying={resumePlaying} Start={startPosition}");
                try
                {
                    await RestartPlaybackWorkerCoreAsync(startPosition, resumePlaying, operationToken).ConfigureAwait(false);
                    FluxMediaLog.Write(LogLevel.Information, ComponentName, InstanceId, $"Event=NetworkRecoveryCompleted Attempt={attempt} ResumePlaying={resumePlaying}");
                    return;
                }
                catch (OperationCanceledException) when (operationToken.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    reason = ex.Message;
                    _lastNetworkError = reason;
                    FluxMediaLog.Write(LogLevel.Warning, ComponentName, InstanceId, $"Event=NetworkRecoveryAttemptFailed Attempt={attempt}", ex);
                    if (_networkRecoveryAttemptCount < Math.Max(0, _currentOptions.NetworkReconnectAttempts))
                        await Task.Delay(TimeSpan.FromSeconds(Math.Max(0, _currentOptions.Network.ReconnectDelayMax)), operationToken).ConfigureAwait(false);
                }
            }

            operationToken.ThrowIfCancellationRequested();
            await StopRecordingCoreAsync(CancellationToken.None).ConfigureAwait(false);
            await RetirePipelineAsync().ConfigureAwait(false);

            await TransitionAsync(PlaybackState.Failed, operationToken).ConfigureAwait(false);
            NotifyFault(CreateError(PlaybackError.PlayFailed, "网络播放恢复失败，已达到重试上限或关闭了重连。"), new IOException(reason));
        }
        catch (OperationCanceledException) when (operationToken.IsCancellationRequested)
        {
            FluxMediaLog.Write(LogLevel.Debug, ComponentName, InstanceId, $"Event=NetworkRecoveryCanceled State={State}");
        }
        catch (Exception ex)
        {
            _lastNetworkError = ex.Message;
            FluxMediaLog.Write(LogLevel.Error, ComponentName, InstanceId, "Event=NetworkRecoveryFailed", ex);
        }
        finally
        {
            if (gateEntered) _pipelineMutationGate.Release();
        }
    }

    /// <summary>
    /// 保存最新音频工作线程状态。
    /// </summary>
    private void UpdateAudioDiagnostics(AudioPlaybackDiagnostics diagnostic)
    {
        _audioDiagnostic = diagnostic;
    }

    /// <summary>
    /// 更新打开媒体时的耗时和流信息。
    /// </summary>
    private void UpdateOpenDiagnostics(PlaybackDiagnosticInfo diagnostic)
    {
        _diagnostic = GetDiagnosticInfo() with
        {
            SourceKind = diagnostic.SourceKind,
            SourceUrl = diagnostic.SourceUrl,
            FFmpegOpenOptions = diagnostic.FFmpegOpenOptions,
            OpenElapsed = diagnostic.OpenElapsed,
            StreamInfoElapsed = diagnostic.StreamInfoElapsed,
            Protocol = diagnostic.Protocol,
            Container = diagnostic.Container,
            FrameRate = diagnostic.FrameRate,
            TimingMode = diagnostic.TimingMode,
            LowLatencyEnabled = diagnostic.LowLatencyEnabled,
            CanSeek = diagnostic.CanSeek,
            HasDuration = diagnostic.HasDuration
        };
    }

    /// <summary>
    /// 更新视频解码线程的统计和运行状态。
    /// </summary>
    private void UpdateWorkerDiagnostics(PlaybackWorkerDiagnostics worker)
    {
        _diagnostic = GetDiagnosticInfo() with
        {
            SourceKind = worker.SourceKind,
            SourceUrl = worker.SourceUrl,
            FFmpegOpenOptions = worker.OpenOptions ?? _diagnostic.FFmpegOpenOptions,
            OpenElapsed = worker.OpenElapsed ?? _diagnostic.OpenElapsed,
            StreamInfoElapsed = worker.StreamInfoElapsed ?? _diagnostic.StreamInfoElapsed,
            FirstFrameElapsed = worker.FirstFrameElapsed ?? _diagnostic.FirstFrameElapsed,
            FirstDecodedFrameElapsed = worker.FirstDecodedFrameElapsed ?? _diagnostic.FirstDecodedFrameElapsed,
            Protocol = worker.Protocol ?? _diagnostic.Protocol,
            Container = worker.Container ?? _diagnostic.Container,
            FrameRate = worker.FrameRate ?? _diagnostic.FrameRate,
            TimingMode = worker.TimingMode,
            LowLatencyEnabled = worker.LowLatencyEnabled,
            ReadPacketCount = worker.ReadPacketCount,
            DecodedFrameCount = worker.DecodedFrameCount,
            PushedFrameCount = worker.PushedFrameCount,
            DroppedFrameCount = worker.DroppedVideoFrameCount,
            BufferingDecisionCount = worker.BufferingDecisionCount,
            VideoBufferedDuration = worker.LastVideoBufferedDuration,
            LowLatencyCatchUpActive = worker.LowLatencyCatchUpActive,
            LastVideoAudioDistance = worker.LastVideoAudioDistance,
            LastAudioClock = worker.LastAudioClock,
            DroppedNonVideoPacketCount = worker.DroppedNonVideoPacketCount,
            LastFrameAt = worker.LastFrameAt ?? _diagnostic.LastFrameAt,
            WorkerRunning = worker.WorkerRunning,
            WorkerExitReason = worker.ExitReason ?? _diagnostic.WorkerExitReason,
            LastFFmpegErrorCode = worker.LastFFmpegErrorCode ?? _diagnostic.LastFFmpegErrorCode,
            LastFFmpegError = worker.LastFFmpegError ?? _diagnostic.LastFFmpegError,
            HardwareAccelerationRequested = worker.HardwareAccelerationRequested,
            HardwareAccelerationActive = worker.HardwareAccelerationActive,
            VideoDecoderBackend = worker.VideoDecoderBackend,
            VideoPresentationBackend = worker.VideoPresentationBackend,
            HardwareAccelerationFailureReason = worker.HardwareAccelerationFailureReason
        };
    }

    /// <summary>
    /// 记录播放生命周期事件及发生时间。
    /// </summary>
    private void MarkLifecycle(string eventName)
    {
        var now = DateTimeOffset.UtcNow;
        _lifecycleDiagnostic = _lifecycleDiagnostic with
        {
            LastEvent = eventName,
            LastEventAt = now,
            OpenedAt = eventName == "OpenCompleted" ? now : _lifecycleDiagnostic.OpenedAt,
            PlayStartedAt = eventName == "PlayStarted" ? now : _lifecycleDiagnostic.PlayStartedAt,
            FirstUiFrameAt = eventName == "FirstUiFrame" ? now : _lifecycleDiagnostic.FirstUiFrameAt,
            CompletedAt = eventName == "Completed" ? now : _lifecycleDiagnostic.CompletedAt,
            StopStartedAt = eventName == "StopStarted" ? now : _lifecycleDiagnostic.StopStartedAt,
            StopCompletedAt = eventName == "StopCompleted" ? now : _lifecycleDiagnostic.StopCompletedAt,
            PlayCount = eventName == "PlayStarted" ? _lifecycleDiagnostic.PlayCount + 1 : _lifecycleDiagnostic.PlayCount,
            PauseCount = eventName == "Paused" ? _lifecycleDiagnostic.PauseCount + 1 : _lifecycleDiagnostic.PauseCount,
            StopCount = eventName == "StopCompleted" ? _lifecycleDiagnostic.StopCount + 1 : _lifecycleDiagnostic.StopCount,
            NaturalCompletionCount = eventName == "Completed" ? _lifecycleDiagnostic.NaturalCompletionCount + 1 : _lifecycleDiagnostic.NaturalCompletionCount
        };
        FluxMediaLog.Write(LogLevel.Information, ComponentName, InstanceId, $"Event=PlaybackLifecycle EventName={eventName} State={State} Position={_currentTime} PlayCount={_lifecycleDiagnostic.PlayCount} PauseCount={_lifecycleDiagnostic.PauseCount} StopCount={_lifecycleDiagnostic.StopCount} Completed={_lifecycleDiagnostic.NaturalCompletionCount}");
    }

    /// <summary>
    /// 根据帧输出和同步状态生成健康诊断。
    /// </summary>
    private PlaybackHealthDiagnosticInfo BuildHealthDiagnostic(TimeSpan? audioVideoDrift)
    {
        var now = DateTimeOffset.UtcNow;
        var lastUiFrameAt = _lastUiFrameAt ?? _diagnostic.LastUiFrameAt;
        TimeSpan? lastUiFrameAge = lastUiFrameAt is null ? null : now - lastUiFrameAt.Value;
        TimeSpan? lastWorkerFrameAge = _diagnostic.LastFrameAt is null ? null : now - _diagnostic.LastFrameAt.Value;
        var isPlaying = State == PlaybackState.Playing && !_isPaused;
        var hasVideo = CurrentMediaInfo?.HasVideo == true;
        var seeking = _seeking.Diagnostic.InProgress;
        var reason = default(string);

        if (isPlaying && !seeking)
        {
            if (hasVideo && _diagnostic.WorkerRunning && lastUiFrameAge is not null && lastUiFrameAge > TimeSpan.FromSeconds(2))
            {
                reason = "VideoUiStall";
            }
            else if (hasVideo && !_diagnostic.WorkerRunning && _pipeline?.Completion is { IsCompleted: true } && State != PlaybackState.Completed)
            {
                reason = "VideoWorkerStopped";
            }
            else if (!string.IsNullOrWhiteSpace(_audioDiagnostic.LastError))
            {
                reason = "AudioError";
            }
            else if (_diagnostic.LastFFmpegError is not null)
            {
                reason = "FFmpegError";
            }
            else if (Demuxer?.ReadTimeoutCount > 0 && _pipeline?.DemuxerExited == true)
            {
                reason = "NetworkReadTimeout";
            }
            else if (CurrentSource?.Kind == MediaSourceKind.NetworkStream &&
                _pipeline?.DemuxerExited == true &&
                !string.IsNullOrWhiteSpace(Demuxer?.LastError) &&
                State != PlaybackState.Completed)
            {
                reason = "NetworkReadEnded";
            }
        }

        var health = new PlaybackHealthDiagnosticInfo
        {
            IsHealthy = reason is null,
            IsStalled = reason is "VideoUiStall" or "VideoWorkerStopped" or "NetworkReadTimeout" or "NetworkReadEnded",
            Reason = reason,
            LastUiFrameAge = lastUiFrameAge,
            LastWorkerFrameAge = lastWorkerFrameAge,
            VideoBufferedDuration = _diagnostic.VideoBufferedDuration,
            WorkerRunning = _diagnostic.WorkerRunning,
            WorkerExitReason = _diagnostic.WorkerExitReason,
            AudioWorkerState = _audioDiagnostic.WorkerState,
            AudioBufferedDuration = _audioDiagnostic.OutputBufferedDuration,
            AudioDeviceDelay = _audioDiagnostic.OutputDeviceDelay,
            AudioVideoDrift = audioVideoDrift,
            CheckedAt = now
        };

        LogHealthTransition(health);
        _healthDiagnostic = health;
        return health;
    }

    /// <summary>
    /// 整理读取超时和网络恢复状态。
    /// </summary>
    private PlaybackNetworkDiagnosticInfo BuildNetworkDiagnostic()
    {
        var now = DateTimeOffset.UtcNow;
        var lastPacketAt = Demuxer?.LastPacketAt ?? _lastNetworkPacketAt;
        TimeSpan? lastPacketAge = lastPacketAt is null ? null : now - lastPacketAt.Value;
        return new PlaybackNetworkDiagnosticInfo
        {
            IsNetworkSource = CurrentSource?.Kind == MediaSourceKind.NetworkStream,
            ReconnectEnabled = _currentOptions.Network.ReconnectEnabled,
            ConfiguredReconnectAttempts = _currentOptions.Network.ReconnectEnabled ? _currentOptions.NetworkReconnectAttempts : 0,
            RecoveryAttemptCount = Volatile.Read(ref _networkRecoveryAttemptCount),
            ReadTimeoutCount = _retiredReadTimeouts + (Demuxer?.ReadTimeoutCount ?? 0),
            LastPacketAt = lastPacketAt,
            LastPacketAge = lastPacketAge,
            DemuxerState = Demuxer?.State,
            DemuxerExitReason = Demuxer?.ExitReason,
            LastNetworkError = _lastNetworkError ?? Demuxer?.LastError
        };
    }

    /// <summary>
    /// 健康状态发生变化时输出诊断日志。
    /// </summary>
    private void LogHealthTransition(PlaybackHealthDiagnosticInfo health)
    {
        var signature = $"{health.IsHealthy}|{health.Reason ?? "-"}|{health.WorkerRunning}|{health.AudioWorkerState ?? "-"}|{health.WorkerExitReason ?? "-"}";
        if (signature == _lastHealthSignature) return;
        _lastHealthSignature = signature;
        FluxMediaLog.Write(
            health.IsHealthy ? LogLevel.Information : LogLevel.Warning, ComponentName, InstanceId, $"Event=PlaybackHealthChanged Healthy={health.IsHealthy} Stalled={health.IsStalled} Reason={health.Reason ?? "-"} LastUiFrameAge={health.LastUiFrameAge?.ToString() ?? "-"} LastWorkerFrameAge={health.LastWorkerFrameAge?.ToString() ?? "-"} VideoBuffered={health.VideoBufferedDuration?.ToString() ?? "-"} WorkerRunning={health.WorkerRunning} WorkerExit={health.WorkerExitReason ?? "-"} AudioState={health.AudioWorkerState ?? "-"} AudioBuffered={health.AudioBufferedDuration?.ToString() ?? "-"} AudioDelay={health.AudioDeviceDelay?.ToString() ?? "-"} Drift={health.AudioVideoDrift?.ToString() ?? "-"}");
    }

    /// <summary>
    /// 检查会话是否已打开且未释放。
    /// </summary>
    private void EnsureOpened()
    {
        ThrowIfDisposed();
        if (CurrentSource is null || CurrentMediaInfo is null)
        {
            var error = CreateError(PlaybackError.InvalidState, "尚未打开播放媒体。");
            NotifyFault(error);
            throw new PlaybackException(PlaybackError.InvalidState, error);
        }
    }

    /// <summary>
    /// 构建包含当前会话信息的播放错误。
    /// </summary>
    private FluxMediaError CreateError(PlaybackError playbackError, string message)
    {
        return new FluxMediaError(
            playbackError == PlaybackError.ResourceDisposed ? FluxMediaErrorCode.ResourceDisposed : FluxMediaErrorCode.InvalidState, message, ComponentName, InstanceId, State.ToString(), CurrentSource?.Uri);
    }

    /// <summary>
    /// 检查媒体源类型和协议是否受支持。
    /// </summary>
    private static bool IsSupportedPlaybackSource(MediaSource source)
    {
        if (source.Kind == MediaSourceKind.File)
        {
            return !string.IsNullOrWhiteSpace(source.Uri);
        }

        if (source.Kind != MediaSourceKind.NetworkStream || !Uri.TryCreate(source.Uri, UriKind.Absolute, out var uri))
        {
            return false;
        }

        return uri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase)
            || uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase)
            || uri.Scheme.Equals("rtsp", StringComparison.OrdinalIgnoreCase)
            || uri.Scheme.Equals("rtmp", StringComparison.OrdinalIgnoreCase)
            || uri.AbsolutePath.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 从媒体地址提取传输协议。
    /// </summary>
    private static string? ProtocolOf(MediaSource? source)
    {
        if (source is null) return null;
        return Uri.TryCreate(source.Uri, UriKind.Absolute, out var uri) ? uri.Scheme : source.Kind.ToString();
    }

    /// <summary>
    /// 判断当前媒体源是否支持时间跳转。
    /// </summary>
    private bool CanSeekCurrentSource()
    {
        var source = CurrentSource;
        var info = CurrentMediaInfo;
        return source is not null && info is not null && PlaybackTimingPolicy.CanSeek(source, info.Duration);
    }

    /// <summary>
    /// 判断当前媒体是否允许速度和跳转控制。
    /// </summary>
    private bool CanUsePresentationControls()
    {
        if (CurrentSource is null || CurrentMediaInfo is null) return false;
        var timingMode = PlaybackTimingPolicy.Resolve(CurrentSource, _currentOptions, CurrentMediaInfo.Duration);
        return timingMode == PlaybackTimingMode.PresentationTimestamp;
    }

    /// <summary>
    /// 将跳转目标限制在有效媒体范围内。
    /// </summary>
    private TimeSpan ClampPosition(TimeSpan position)
    {
        if (position < TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        if (Duration.HasValue && position > Duration.Value)
        {
            return Duration.Value;
        }

        return position;
    }

    /// <summary>
    /// 记录控制失败并返回失败结果。
    /// </summary>
    private PlaybackResult ControlFailure(PlaybackError error, string message)
    {
        _lastControlError = message;
        FluxMediaLog.Write(LogLevel.Warning, ComponentName, InstanceId, $"Event=PlaybackControlFailed Error={error} Message={message} State={State} SourceKind={CurrentSource?.Kind} Source={CurrentSource?.Uri}");
        return PlaybackResult.Failure(error, message);
    }

    /// <summary>
    /// 检查播放速度是否在支持范围内。
    /// </summary>
    private static bool IsSupportedRate(double rate)
    {
        return Math.Abs(rate - 0.5) < 0.001 ||
            Math.Abs(rate - 1.0) < 0.001 ||
            Math.Abs(rate - 1.5) < 0.001 ||
            Math.Abs(rate - 2.0) < 0.001;
    }

    /// <summary>
    /// 将无效播放速度转换为有效值。
    /// </summary>
    private static double NormalizePlaybackRate(double rate)
    {
        return IsSupportedRate(rate) ? rate : 1.0;
    }

    /// <summary>
    /// 重建停止和跳转共用的取消范围。
    /// </summary>
    private void ResetOperationLifetime()
    {
        _operationCts?.Dispose();
        _operationCts = new CancellationTokenSource();
    }

    /// <summary>
    /// 当前唯一的资源管线。
    /// </summary>
    private PlaybackPipeline? _pipeline;
    /// <summary>
    /// 当前管线的解复用及录制入口。
    /// </summary>
    private FFmpegSharedDemuxer? Demuxer => _pipeline?.Demuxer;
    /// <summary>
    /// 已退役管线累计的读取超时次数。
    /// </summary>
    private int _retiredReadTimeouts;
    /// <summary>
    /// 上一条管线最后收到数据包的时间。
    /// </summary>
    private DateTimeOffset? _lastNetworkPacketAt;
    /// <summary>
    /// 创建新一代管线，旧代通知不会再写入当前会话。
    /// </summary>
    private PlaybackPipeline CreatePipeline(TimeSpan position)
    {
        PlaybackPipeline? pipeline = null;
        var callbacks = new PlaybackPipelineCallbacks(() => _isPaused, () => _playbackRate, () => _volume, () => _isMuted,
            _resyncSignal.Snapshot, () => Volatile.Read(ref _videoOutputCapabilities),
            diagnostic => { if (ReferenceEquals(_pipeline, pipeline)) UpdateWorkerDiagnostics(diagnostic); },
            diagnostic => { if (ReferenceEquals(_pipeline, pipeline)) UpdateAudioDiagnostics(diagnostic); },
            (frame, count) => ReferenceEquals(_pipeline, pipeline) ? PublishVideoFrameAsync(frame, count) : default);
        pipeline = new PlaybackPipeline(CurrentSource!, _currentOptions, InstanceId, position, _videoFrames, callbacks, _operationCts!.Token);
        _pipeline = pipeline;
        return pipeline;
    }
    /// <summary>
    /// 启动管线并独立观察其完成状态。
    /// </summary>
    private void StartPipeline(PlaybackPipeline pipeline)
    {
        _playStartedAt = DateTimeOffset.UtcNow;
        pipeline.Start();
        _ = ObservePipelineAsync(pipeline);
    }
    /// <summary>
    /// 先摘除旧代，再等待线程退出，禁止旧帧污染新代。
    /// </summary>
    private async Task RetirePipelineAsync()
    {
        var pipeline = _pipeline;
        _pipeline = null;
        if (pipeline is null) return;
        _retiredReadTimeouts += pipeline.Demuxer.ReadTimeoutCount;
        _lastNetworkPacketAt = pipeline.Demuxer.LastPacketAt ?? _lastNetworkPacketAt;
        await pipeline.DisposeAsync().ConfigureAwait(false);
        _videoFrames.ClearPublicOutput();
    }

    /// <summary>
    /// 只由当前代触发完成、失败和恢复，通知与公开控制共享一把锁。
    /// </summary>
    private async Task ObservePipelineAsync(PlaybackPipeline pipeline)
    {
        Exception? failure = null;
        try { await pipeline.Completion!.ConfigureAwait(false); }
        catch (Exception exception) { failure = exception; }
        if (!ReferenceEquals(_pipeline, pipeline) || _operationCts?.IsCancellationRequested != false || IsDisposed) return;
        if (failure is OperationCanceledException && pipeline.IsCanceled) return;
        if (failure is not null && CurrentSource?.Kind == MediaSourceKind.NetworkStream && pipeline.Demuxer.State == "Faulted")
        {
            _lastNetworkError = failure.Message;
            await TryRecoverNetworkPlaybackAsync(pipeline, failure.Message).ConfigureAwait(false);
            return;
        }
        await _pipelineMutationGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!ReferenceEquals(_pipeline, pipeline) || _operationCts?.IsCancellationRequested != false || IsDisposed) return;
            await StopRecordingCoreAsync(CancellationToken.None).ConfigureAwait(false);
            if (failure is null)
            {
                MarkLifecycle("Completed");
                await TransitionAsync(PlaybackState.Completed).ConfigureAwait(false);
            }
            else
            {
                await RetirePipelineAsync().ConfigureAwait(false);
                await TransitionAsync(PlaybackState.Failed).ConfigureAwait(false);
                NotifyFault(CreateError(PlaybackError.PlayFailed, $"播放失败：{failure.Message}"), failure);
            }
        }
        finally { _pipelineMutationGate.Release(); }
    }

    /// <summary>
    /// 独立管理跳转合并与诊断。
    /// </summary>
    private readonly PlaybackSeekController _seeking;

    /// <summary>
    /// 跳转执行与打开、播放、停止、重连共用管线锁。
    /// </summary>
    private async Task<PlaybackResult> ExecuteQueuedSeekAsync(TimeSpan target, bool resumePlaying, CancellationToken cancellationToken)
    {
        await _pipelineMutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await ExecuteSeekAsync(target, resumePlaying, cancellationToken).ConfigureAwait(false); }
        finally { _pipelineMutationGate.Release(); }
    }

    /// <summary>
    /// 跨管线保管录制器与预约状态。
    /// </summary>
    private readonly PlaybackRecordingController _recording = new();
    /// <summary>
    /// 管线锁内停止录制，不重复进入锁。
    /// </summary>
    private async ValueTask<PlaybackResult> StopRecordingCoreAsync(CancellationToken cancellationToken)
    {
        if (!_recording.IsRecording) return PlaybackResult.Success();
        var result = await _recording.StopAsync(Demuxer, cancellationToken).ConfigureAwait(false);
        _lastControlOperation = "StopRecording";
        _lastControlError = result.IsSuccess ? null : result.Message;
        return result;
    }

}
