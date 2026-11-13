using LogConverge.Sharp;
using FluxMedia.Core.Errors;
using FluxMedia.Core.Models;
using FluxMedia.Core.State;
using FluxMedia.FFmpeg.Pipeline;
using FluxMedia.Playback.Internal;

namespace FluxMedia.Playback;

/// <summary>
/// 管理媒体打开、播放控制和音视频输出。
/// </summary>
internal sealed partial class PlaybackSession : AsyncMediaStateMachine<PlaybackState>
{
    /// <summary>
    /// 跳转首帧等待上限。
    /// </summary>
    private static readonly TimeSpan SeekFirstFrameTimeout = TimeSpan.FromMilliseconds(1500);

    /// <summary>
    /// 位置通知间隔。
    /// </summary>
    private static readonly TimeSpan PositionNotificationInterval = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// 创建会话时保存的默认配置。
    /// </summary>
    private readonly PlaybackOptions _defaultOptions;

    /// <summary>
    /// 引擎启动时校验通过的共享策略。
    /// </summary>
    private readonly FluxEngineOptions _engineOptions;

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
            if (source is null)
                return new CancellationToken(true);
            try
            {
                return source.Token;
            }
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
    /// 当前播放是否处于暂停状态。
    /// </summary>
    private volatile bool _isPaused;

    /// <summary>
    /// 当前会话的媒体播放位置。
    /// </summary>
    private TimeSpan _currentTime;

    /// <summary>
    /// 重建解码线程时采用的媒体起点。
    /// </summary>
    private TimeSpan _workerStartPosition;

    /// <summary>
    /// 当前媒体的换速请求与唯一实际速度来源。
    /// </summary>
    private readonly PlaybackRateController _rates;

    /// <summary>
    /// 当前音量，范围为 0 到 1。
    /// </summary>
    private double _volume;

    /// <summary>
    /// 是否处于静音状态。
    /// </summary>
    private bool _isMuted;

    /// <summary>
    /// 跳转首帧发布后是否立即暂停。
    /// </summary>
    private bool _pauseAfterSeekFrame;

    /// <summary>
    /// 跳转结束后是否恢复播放。
    /// </summary>
    private bool _seekResumePlayingIntent;

    /// <summary>
    /// 当前跳转实际开始执行的时间。
    /// </summary>
    private PlaybackSeekFrame? _activeSeek;

    /// <summary>
    /// 当前实际执行中的跳转请求编号。
    /// </summary>
    private long ActiveSeekRequestId => _activeSeek?.RequestId ?? 0;

    /// <summary>
    /// 显示端最新声明的 GPU 输出能力。
    /// </summary>
    private VideoOutputCapabilities _videoOutputCapabilities = new();

    /// <summary>
    /// 初始化播放会话所需的状态和资源。
    /// </summary>
    public PlaybackSession(FluxEngineOptions engineOptions, PlaybackOptions? defaultOptions = null) : base(PlaybackState.Created, nameof(PlaybackSession))
    {
        _engineOptions = engineOptions;
        _defaultOptions = defaultOptions ?? new PlaybackOptions();
        NetworkPlaybackOptions.Validate(_defaultOptions.Network);
        if (!IsFiniteVolume(_defaultOptions.InitialVolume))
            throw new ArgumentOutOfRangeException(nameof(PlaybackOptions.InitialVolume), _defaultOptions.InitialVolume,
                "初始音量必须为有限值，不能为 NaN 或无穷值。");
        _currentOptions = _defaultOptions;
        _rates = new PlaybackRateController(_engineOptions, InstanceId, () => _isPaused);
        _rates.Changed += OnPlaybackRateChanged;
        _rates.CommitFaulted += OnRateCommitFaulted;
        _rates.Reset(_defaultOptions);
        _volume = Math.Clamp(_defaultOptions.InitialVolume, 0, 1);
        _isMuted = _defaultOptions.InitialMuted;
        _videoFrames = new PlaybackVideoFrameHub();
        _seeking = new PlaybackSeekController(ExecuteQueuedSeekAsync);
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
            PlaybackState.Ready => nextState is PlaybackState.Playing or PlaybackState.Paused or PlaybackState.Seeking or PlaybackState.Failed,
            PlaybackState.Playing => nextState is PlaybackState.Paused or PlaybackState.Seeking or PlaybackState.Completed or PlaybackState.Failed,
            PlaybackState.Paused => nextState is PlaybackState.Playing or PlaybackState.Seeking or PlaybackState.Completed or PlaybackState.Failed,
            PlaybackState.Seeking => nextState is PlaybackState.Playing or PlaybackState.Paused or PlaybackState.Completed or PlaybackState.Failed,
            PlaybackState.Stopping => nextState is PlaybackState.Stopped or PlaybackState.Failed,
            PlaybackState.Stopped => nextState is PlaybackState.Opening or PlaybackState.Playing or PlaybackState.Paused or PlaybackState.Seeking or PlaybackState.Failed,
            PlaybackState.Completed => nextState is PlaybackState.Playing or PlaybackState.Paused or PlaybackState.Seeking or PlaybackState.Failed,
            PlaybackState.Failed => nextState == PlaybackState.Opening,
            PlaybackState.Disposed => false,
            _ => false
        };
    }

    /// <summary>
    /// 当前已打开的本地文件或网络媒体源。
    /// </summary>
    public MediaSource? CurrentSource
    {
        get; private set;
    }

    /// <summary>
    /// 当前媒体的容器和音视频流信息。
    /// </summary>
    public MediaInfo? CurrentMediaInfo
    {
        get; private set;
    }

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
    public bool CanSeek => !IsDisposed && State is not PlaybackState.Created and not PlaybackState.Opening and not PlaybackState.Stopping and not PlaybackState.Disposed and not PlaybackState.Failed && CanSeekCurrentSource();

    /// <summary>
    /// 播放速度。
    /// </summary>
    public double PlaybackRate => _rates.Snapshot.AppliedRate;

    /// <summary>
    /// 已确认的点播媒体允许倍速。
    /// </summary>
    public bool CanChangeSpeed => CanSeek;

    /// <summary>
    /// 音量。
    /// </summary>
    public double Volume => _volume;

    /// <summary>
    /// 是否静音。
    /// </summary>
    public bool IsMuted => _isMuted;

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
        if (!MediaSourcePolicy.TryValidate(request?.Source, out var source, out var validationError))
        {
            var error = CreateError(PlaybackError.OpenFailed, validationError) with
            {
                MediaSource = request?.Source?.Uri
            };
            NotifyFault(error);
            throw new PlaybackException(PlaybackError.OpenFailed, error);
        }
        cancellationToken.ThrowIfCancellationRequested();
        NetworkPlaybackOptions.Validate((request!.Options ?? _defaultOptions).Network);
        PlaybackRateController.ValidateOptions(request.Options ?? _defaultOptions);
        _operationCts?.Cancel();
        _rates.Retire(preservePending: false);
        await _pipelineMutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var stopResult = await StopCoreAsync(CancellationToken.None).ConfigureAwait(false);
            ThrowIfRecordingStopFailed(stopResult);
            ResetOperationLifetime();
            if (cancelVersion != Volatile.Read(ref _cancelVersion))
                _operationCts!.Cancel();
            await TransitionAsync(PlaybackState.Opening, cancellationToken).ConfigureAwait(false);
            CurrentSource = source;
            CurrentMediaInfo = null;
            _currentOptions = request!.Options ?? _defaultOptions;
            _rates.Reset(_currentOptions);
            _positionNotifications.Reset();
            _currentTime = TimeSpan.Zero;
            _workerStartPosition = TimeSpan.Zero;
            _pauseAfterSeekFrame = false;
            _seeking.Reset();
            _resyncSignal.Request(TimeSpan.Zero);
            _activeSeek = null;
            _recording.Reset();
            _videoFrames.ResetPublicOutput();
            try
            {
                var timeout = source.Kind == MediaSourceKind.NetworkStream ? _currentOptions.Network.OpenTimeout : _currentOptions.OpenTimeout;
                using var open = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _operationCts!.Token);
                open.CancelAfter(timeout);
                var pipeline = CreatePipeline(TimeSpan.Zero);
                await pipeline.PrepareAsync(open.Token).ConfigureAwait(false);
                CurrentMediaInfo = pipeline.Demuxer.MediaInfo ?? throw new InvalidOperationException("未读取到媒体信息。");
                if (!CurrentMediaInfo.HasVideo)
                    throw new InvalidOperationException("当前媒体没有视频流。");
                ConfigureInitialRate();
                await TransitionAsync(PlaybackState.Ready, cancellationToken).ConfigureAwait(false);
                Log.Info(ComponentName,
                    $"SessionId={InstanceId} Event=MediaOpened SourceKind={source.Kind} IsLive={IsLive} Duration={Duration}");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || _operationCts?.IsCancellationRequested != false)
            {
                await StopCoreAsync(CancellationToken.None).ConfigureAwait(false);
                throw;
            }
            catch (Exception exception)
            {
                var cleanup = await RetirePipelineAsync().ConfigureAwait(false);
                var failure = cleanup is null ? exception : new AggregateException("打开媒体失败，管线清理也发生错误。", exception, cleanup);
                _rates.Retire(preservePending: false);
                await TransitionAsync(PlaybackState.Failed).ConfigureAwait(false);
                var error = CreateError(PlaybackError.OpenFailed, exception is OperationCanceledException ? "打开媒体超时。" : $"打开媒体失败：{exception.Message}");
                NotifyFault(error, failure);
                throw new PlaybackException(PlaybackError.OpenFailed, error, failure);
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
            if (cancelVersion != Volatile.Read(ref _cancelVersion))
                throw new OperationCanceledException();
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
        if (State == PlaybackState.Failed)
            return ControlFailure(PlaybackError.InvalidState, "播放已失败，请重新打开媒体。");
        if (State == PlaybackState.Playing && _pipeline is { IsCanceled: false, Completion.IsCompleted: false })
            return PlaybackResult.Success();
        if (_operationCts is null || _operationCts.IsCancellationRequested)
            ResetOperationLifetime();
        _isPaused = false;
        _rates.Wake();
        _pauseAfterSeekFrame = false;
        if (_pipeline is null || _pipeline.IsCanceled || _pipeline.Completion?.IsCompleted == true)
        {
            var position = State == PlaybackState.Completed ? TimeSpan.Zero : _workerStartPosition;
            await RestartPlaybackWorkerCoreAsync(position, true, cancellationToken).ConfigureAwait(false);
        }
        else if (_pipeline.Completion is null)
        {
            await _recording.AttachAsync(_pipeline.Demuxer).ConfigureAwait(false);
            await TransitionAsync(PlaybackState.Playing, cancellationToken).ConfigureAwait(false);
            StartPipeline(_pipeline);
        }
        else
            await TransitionAsync(PlaybackState.Playing, cancellationToken).ConfigureAwait(false);
        return PlaybackResult.Success();
    }

    /// <summary>
    /// 暂停播放并保留当前位置。
    /// </summary>
    public async ValueTask<PlaybackResult> PauseAsync(CancellationToken cancellationToken = default)
    {
        var cancelVersion = Volatile.Read(ref _cancelVersion);
        var completion = _rates.Suspend(discardPending: false);
        try
        {
            await WaitForRateBarrierAsync(completion, cancellationToken).ConfigureAwait(false);
            await _pipelineMutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (cancelVersion != Volatile.Read(ref _cancelVersion))
                    throw new OperationCanceledException();
                if (State == PlaybackState.Failed)
                    return ControlFailure(PlaybackError.InvalidState, "播放已失败。");
                return await PauseCoreAsync(cancellationToken).ConfigureAwait(false);
            }
            finally { _pipelineMutationGate.Release(); }
        }
        finally { _rates.Resume(); }
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
        await TransitionAsync(PlaybackState.Paused, cancellationToken).ConfigureAwait(false);
        return PlaybackResult.Success();
    }

    /// <summary>
    /// 恢复暂停的播放。
    /// </summary>
    public ValueTask<PlaybackResult> ResumeAsync(CancellationToken cancellationToken = default)
    {
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
            return PlaybackResult.Failure(PlaybackError.NotSeekable, "当前媒体不允许调整播放进度。");
        }
        var startedAt = DateTimeOffset.UtcNow;
        _resyncSignal.InvalidateFrames();
        _videoFrames.ClearPublicOutput();
        _activeSeek = new PlaybackSeekFrame(_seeking.Operation.RequestId, target, startedAt);
        _seeking.Begin();
        await TransitionAsync(PlaybackState.Seeking, cancellationToken).ConfigureAwait(false);
        var recordingSuspended = _recording.HasRecording && Demuxer is not null;
        if (recordingSuspended)
        {
            Demuxer!.SuspendRecordingForDiscontinuity();
        }
        try
        {
            if (IsAtDurationEnd(target))
            {
                ApplyTerminalSeek(target, resumePlaying, startedAt);
                Demuxer?.ResumeRecordingAfterDiscontinuity();
                recordingSuspended = false;
                await TransitionAsync(resumePlaying ? PlaybackState.Completed : PlaybackState.Paused, cancellationToken).ConfigureAwait(false);
                return PlaybackResult.Success();
            }

            if (!await TryDemuxerSeekAsync(target, resumePlaying, cancellationToken).ConfigureAwait(false))
            {
                await RestartPlaybackWorkerCoreAsync(target, resumePlaying, cancellationToken).ConfigureAwait(false);
                _seeking.SetPath(PlaybackSeekPath.FullRestart, null);
            }

            Demuxer?.ResumeRecordingAfterDiscontinuity();
            recordingSuspended = false;

            if (!await WaitForSeekFirstFrameOrRecoverAsync(target, resumePlaying, cancellationToken).ConfigureAwait(false))
            {
                InvokeSubscribersSafely(PositionChanged, new PlaybackPositionChangedEventArgs(target, Duration), nameof(PositionChanged));
                await RestorePostSeekStateAsync(resumePlaying).ConfigureAwait(false);
                _seeking.Complete(DateTimeOffset.UtcNow - startedAt);
                return PlaybackResult.Failure(PlaybackError.PlayFailed, "Seek 后未能恢复视频首帧。");
            }

            InvokeSubscribersSafely(PositionChanged, new PlaybackPositionChangedEventArgs(target, Duration), nameof(PositionChanged));
            await RestorePostSeekStateAsync(resumePlaying).ConfigureAwait(false);
            _seeking.Complete(DateTimeOffset.UtcNow - startedAt);
            return PlaybackResult.Success();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            if (_pipeline?.IsCanceled == false && _resyncSignal.Snapshot().FrameEpoch?.IsCurrent == false)
                _resyncSignal.Request(_currentTime);
            await RestorePostSeekStateAsync(resumePlaying).ConfigureAwait(false);
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
        _seeking.SetPath(PlaybackSeekPath.EndOfMedia, null);
        _seeking.Complete(DateTimeOffset.UtcNow - startedAt);
    }

    /// <summary>
    /// 按原始播放意图恢复跳转后的状态。
    /// </summary>
    private async Task RestorePostSeekStateAsync(bool resumePlaying)
    {
        if (State is PlaybackState.Stopping or PlaybackState.Stopped or PlaybackState.Disposed || _operationCts?.IsCancellationRequested == true)
        {
            return;
        }

        _isPaused = !resumePlaying;
        _pauseAfterSeekFrame = false;
        _seekResumePlayingIntent = resumePlaying;
        if (State == PlaybackState.Seeking)
        {
            await TransitionAsync(resumePlaying ? PlaybackState.Playing : PlaybackState.Paused, CancellationToken.None).ConfigureAwait(false);
        }
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
    public async ValueTask<PlaybackResult> SetPlaybackRateAsync(double rate, CancellationToken cancellationToken = default)
    {
        PlaybackRateRequest request;
        await _pipelineMutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureOpened();
            cancellationToken.ThrowIfCancellationRequested();
            if (State is (PlaybackState.Playing or PlaybackState.Paused)
                && (OperationToken.IsCancellationRequested || _pipeline?.IsCanceled == true))
                return ControlFailure(PlaybackError.OperationCanceled, "播放操作正在结束。");
            request = _rates.Submit(rate, _isPaused || State != PlaybackState.Playing, CanChangeSpeed,
                IsLive ? "直播不支持倍速。" : "未知时长或当前媒体不支持倍速播放。");
        }
        finally { _pipelineMutationGate.Release(); }
        // 登记完成即释放管线锁；提交回调也不需要取得这把锁。
        return await _rates.WaitAsync(request, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 设置音量，范围为 0 到 1。
    /// </summary>
    public ValueTask<PlaybackResult> SetVolumeAsync(double volume, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsFiniteVolume(volume) || volume is < 0 or > 1)
        {
            var error = CreateError(PlaybackError.InvalidState, "音量必须为 0 到 1 之间的有限值。");
            NotifyFault(error);
            throw new PlaybackException(PlaybackError.InvalidState, error);
        }

        _volume = volume;
        _pipeline?.SetVolume(_volume, _isMuted);
        return new ValueTask<PlaybackResult>(PlaybackResult.Success());
    }

    /// <summary>
    /// 初始配置与运行时音量共用有限值检查。
    /// </summary>
    private static bool IsFiniteVolume(double volume) => !double.IsNaN(volume) && !double.IsInfinity(volume);

    /// <summary>
    /// 设置静音状态。
    /// </summary>
    public ValueTask<PlaybackResult> SetMutedAsync(bool muted, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _isMuted = muted;
        _pipeline?.SetVolume(_volume, _isMuted);
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
            if (CurrentMediaInfo?.HasVideo != true)
                return ControlFailure(PlaybackError.RecordingFailed, "当前媒体没有视频流。");
            if (State is PlaybackState.Created or PlaybackState.Opening or PlaybackState.Failed or PlaybackState.Stopping or PlaybackState.Disposed)
                return ControlFailure(PlaybackError.InvalidState, "当前状态无法开始录制。");
            var result = await _recording.StartAsync(options, Demuxer).ConfigureAwait(false);
            if (!result.IsSuccess)
                Log.Error(ComponentName, $"SessionId={InstanceId} Event=RecordingStartFailed Message={result.Message}");
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
        try
        {
            return await StopRecordingCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { _pipelineMutationGate.Release(); }
    }

    /// <summary>
    /// 录制业务状态通知，不依赖播放进度或诊断查询。
    /// </summary>
    public event Action? RecordingChanged
    {
        add => _recording.StateChanged += value;
        remove => _recording.StateChanged -= value;
    }

    /// <summary>
    /// 读取实际录制状态，不访问输出文件。
    /// </summary>
    public PlaybackRecordingState RecordingState => _recording.GetResult().State;

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
    /// 取消待执行操作并停止播放管线。
    /// </summary>
    public async ValueTask StopAsync(CancellationToken cancellationToken = default)
    {
        var result = await StopPlaybackAsync(cancellationToken).ConfigureAwait(false);
        ThrowIfRecordingStopFailed(result);
    }

    /// <summary>
    /// 取消操作并串行结束当前媒体。
    /// </summary>
    private async ValueTask<PlaybackResult> StopPlaybackAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CancelPendingOperations();
        await _pipelineMutationGate.WaitAsync().ConfigureAwait(false);
        try
        {
            return await StopCoreAsync(CancellationToken.None).ConfigureAwait(false);
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
        try
        {
            _operationCts?.Cancel();
        }
        catch (ObjectDisposedException) { }
        _seeking.CancelPending(new OperationCanceledException("播放操作已取消。"));
        _rates.Retire(preservePending: false);
    }

    /// <summary>
    /// 释放管线并返回录制收尾结果。
    /// </summary>
    private async ValueTask<PlaybackResult> StopCoreAsync(CancellationToken cancellationToken)
    {
        _rates.Retire(preservePending: false);
        if (IsDisposed || State == PlaybackState.Disposed)
            return PlaybackResult.Success();
        if (State is PlaybackState.Created or PlaybackState.Stopped)
        {
            var result = await StopRecordingCoreAsync(CancellationToken.None).ConfigureAwait(false);
            var stoppedCleanup = await RetirePipelineAsync().ConfigureAwait(false);
            if (stoppedCleanup is not null)
            {
                NotifyFault(CreateError(PlaybackError.PlayFailed, "停止播放时资源清理失败。"), stoppedCleanup);
                throw stoppedCleanup;
            }
            return result;
        }
        await TransitionAsync(PlaybackState.Stopping, cancellationToken).ConfigureAwait(false);
        _seeking.CancelPending(new OperationCanceledException("Playback has been stopped."));
        var recordingResult = await StopRecordingCoreAsync(CancellationToken.None).ConfigureAwait(false);
        _isPaused = false;
        _pauseAfterSeekFrame = false;
        var cleanupFailure = await RetirePipelineAsync().ConfigureAwait(false);
        try
        {
            _videoFrames.ResetPublicOutput();
        }
        catch (Exception exception)
        {
            cleanupFailure = cleanupFailure is null ? exception : new AggregateException("管线和视频输出清理均失败。", cleanupFailure, exception);
        }
        // 先收敛停止状态，再向调用方反馈清理错误。
        await TransitionAsync(PlaybackState.Stopped).ConfigureAwait(false);
        if (cleanupFailure is not null)
        {
            NotifyFault(CreateError(PlaybackError.PlayFailed, "停止播放时资源清理失败。"), cleanupFailure);
            throw cleanupFailure;
        }
        return recordingResult;
    }

    /// <summary>
    /// 清理完成后显式反馈录制失败。
    /// </summary>
    private void ThrowIfRecordingStopFailed(PlaybackResult result)
    {
        if (!result.IsSuccess)
        {
            throw new PlaybackException(result.Error, CreateError(result.Error, result.Message ?? "录制收尾失败。"));
        }
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
        var errors = new List<Exception>();
        try
        {
            await StopPlaybackAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) { errors.Add(exception); }
        try
        {
            await _seeking.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception) { errors.Add(exception); }
        try
        {
            _operationCts?.Dispose();
        }
        catch (Exception exception) { errors.Add(exception); }
        try
        {
            _videoFrames.ResetPublicOutput();
        }
        catch (Exception exception) { errors.Add(exception); }
        try
        {
            await TransitionAsync(PlaybackState.Disposed).ConfigureAwait(false);
        }
        catch (Exception exception) { errors.Add(exception); }

        if (errors.Count > 0)
            throw new AggregateException("播放会话资源释放失败。", errors);
    }

    /// <summary>
    /// 发布视频帧，并通知进度与跳转首帧。
    /// </summary>
    private ValueTask PublishVideoFrameAsync(VideoFrame frame)
    {
        if (!frame.IsCurrent)
            return default;
        var seek = _activeSeek;
        _currentTime = frame.Timestamp;
        var now = DateTimeOffset.UtcNow;
        if (_positionNotifications.ShouldPublish(now))
        {
            InvokeSubscribersSafely(PositionChanged, new PlaybackPositionChangedEventArgs(_currentTime, Duration), nameof(PositionChanged));
        }
        InvokeSubscribersSafely(VideoFrameReady, new PlaybackVideoFrameEventArgs(frame), nameof(VideoFrameReady));
        seek?.TryAccept(frame);

        if (_pauseAfterSeekFrame && (seek is null || seek.FirstFrame.IsCompletedSuccessfully))
        {
            _pauseAfterSeekFrame = false;
            _isPaused = true;
        }
        return default;
    }

    /// <summary>
    /// 录制器由会话接管，管线重建失败后仍可继续下一次恢复。
    /// </summary>
    private async Task RestartPlaybackWorkerCoreAsync(TimeSpan startPosition, bool resumePlaying, CancellationToken cancellationToken)
    {
        _recording.Detach(Demuxer);
        var retiredFailure = await RetirePipelineAsync().ConfigureAwait(false);
        if (retiredFailure is not null)
        {
            await StopRecordingCoreAsync(CancellationToken.None).ConfigureAwait(false);
            await TransitionAsync(PlaybackState.Stopping).ConfigureAwait(false);
            await TransitionAsync(PlaybackState.Stopped).ConfigureAwait(false);
            NotifyFault(CreateError(PlaybackError.PlayFailed, "重建前的管线清理失败。"), retiredFailure);
            throw retiredFailure;
        }
        cancellationToken.ThrowIfCancellationRequested();
        var synchronization = _resyncSignal.Request(startPosition);
        if (synchronization.FrameEpoch is { } epoch)
            _activeSeek?.BindEpoch(epoch);
        _workerStartPosition = startPosition;
        _currentTime = startPosition;
        _isPaused = false;
        _pauseAfterSeekFrame = !resumePlaying;
        try
        {
            var pipeline = CreatePipeline(startPosition);
            await pipeline.PrepareAsync(cancellationToken).ConfigureAwait(false);
            await _recording.AttachAsync(pipeline.Demuxer).ConfigureAwait(false);
            if (State is PlaybackState.Ready or PlaybackState.Stopped or PlaybackState.Completed)
                await TransitionAsync(resumePlaying ? PlaybackState.Playing : PlaybackState.Paused, cancellationToken).ConfigureAwait(false);
            StartPipeline(pipeline);
        }
        catch (Exception exception)
        {
            var cleanup = await RetirePipelineAsync().ConfigureAwait(false);
            if (_operationCts?.IsCancellationRequested == false)
            {
                _isPaused = !resumePlaying;
                _pauseAfterSeekFrame = false;
                await TransitionAsync(PlaybackState.Stopping).ConfigureAwait(false);
                await TransitionAsync(PlaybackState.Stopped).ConfigureAwait(false);
            }
            if (cleanup is not null)
                throw new AggregateException("管线重建失败，清理也发生错误。", exception, cleanup);
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
            return false;
        try
        {
            _isPaused = true;
            _pipeline?.ClearAudio();
            _videoFrames.ClearPublicOutput();
            var demuxStartedAt = DateTimeOffset.UtcNow;
            await demuxer.SeekAsync(target, cancellationToken).ConfigureAwait(false);
            var demuxElapsed = DateTimeOffset.UtcNow - demuxStartedAt;
            ApplyActiveDecoderResync(target, resumePlaying);
            _seeking.SetPath(PlaybackSeekPath.Demuxer, demuxElapsed);
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// 通知音视频解码器清空旧状态并重新同步。
    /// </summary>
    private void ApplyActiveDecoderResync(TimeSpan position, bool resumePlaying)
    {
        _pipeline?.ClearAudio();
        _videoFrames.ClearPublicOutput();
        var request = _resyncSignal.Request(position);
        _rates.Resync(request.Version);
        if (request.FrameEpoch is { } epoch)
            _activeSeek?.BindEpoch(epoch);
        _workerStartPosition = position;
        _currentTime = position;
        _isPaused = false;
        _pauseAfterSeekFrame = !resumePlaying;
    }

    /// <summary>
    /// 等待跳转首帧，超时时重建管线恢复。
    /// </summary>
    private async Task<bool> WaitForSeekFirstFrameOrRecoverAsync(TimeSpan target, bool resumePlaying, CancellationToken cancellationToken)
    {
        if (CurrentMediaInfo?.HasVideo != true)
        {
            return true;
        }

        if (await WaitForSeekFirstFrameAsync(SeekFirstFrameTimeout, cancellationToken).ConfigureAwait(false))
        {
            return true;
        }
        if (_seeking.Operation.Path == PlaybackSeekPath.FullRestart ||
            _seeking.Operation.Path == PlaybackSeekPath.FullRestartRecovery)
        {
            return false;
        }

        _seeking.MarkRecoveryAttempted();
        await RestartPlaybackWorkerCoreAsync(target, resumePlaying, cancellationToken).ConfigureAwait(false);
        _seeking.SetPath(PlaybackSeekPath.FullRestartRecovery, null);
        return await WaitForSeekFirstFrameAsync(SeekFirstFrameTimeout, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 等待本次跳转的核心首帧确认。
    /// </summary>
    private async Task<bool> WaitForSeekFirstFrameAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var seek = _activeSeek;
        var firstFrame = seek?.FirstFrame;
        if (firstFrame is null)
            return false;
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var deadline = Task.Delay(timeout, wait.Token);
        var completed = await Task.WhenAny(firstFrame, deadline).ConfigureAwait(false);
        if (completed == firstFrame)
        {
            wait.Cancel();
            var accepted = await firstFrame.ConfigureAwait(false);
            _seeking.RecordFirstFrame(seek!.RequestId, accepted.Elapsed);
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
        if (operation is null || operation.IsCancellationRequested)
            return;
        CancellationToken operationToken;
        try
        {
            operationToken = operation.Token;
        }
        catch (ObjectDisposedException) { return; }
        var gateEntered = false;
        try
        {
            await _pipelineMutationGate.WaitAsync(operationToken).ConfigureAwait(false);
            gateEntered = true;
            // 获取锁后重新校验，防止旧管线重复恢复或覆盖新媒体。
            if (!ReferenceEquals(_pipeline, failedPipeline) || !ReferenceEquals(_operationCts, operation) || IsDisposed || State is PlaybackState.Stopping or PlaybackState.Stopped or PlaybackState.Disposed or PlaybackState.Failed)
                return;

            var attempt = 0;
            var maxAttempts = Math.Max(0, _currentOptions.NetworkReconnectAttempts);
            while (_currentOptions.Network.ReconnectEnabled && attempt < maxAttempts)
            {
                operationToken.ThrowIfCancellationRequested();
                attempt++;
                var resumePlaying = State != PlaybackState.Paused && !_isPaused;
                var startPosition = CanSeekCurrentSource() ? _currentTime : TimeSpan.Zero;
                Log.Warning(ComponentName,
                    $"SessionId={InstanceId} Event=NetworkRecoveryStart Attempt={attempt} Reason={reason} ResumePlaying={resumePlaying} Start={startPosition}");
                try
                {
                    await RestartPlaybackWorkerCoreAsync(startPosition, resumePlaying, operationToken).ConfigureAwait(false);
                    Log.Info(ComponentName, $"SessionId={InstanceId} Event=NetworkRecovered Attempt={attempt}");
                    return;
                }
                catch (OperationCanceledException) when (operationToken.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    reason = ex.Message;
                    Log.Write(LogLevel.Warning, ComponentName,
                        $"SessionId={InstanceId} Event=NetworkRecoveryAttemptFailed Attempt={attempt}", ex);
                    if (attempt < maxAttempts)
                        await Task.Delay(_currentOptions.Network.ReconnectDelay, operationToken).ConfigureAwait(false);
                }
            }

            operationToken.ThrowIfCancellationRequested();
            await StopRecordingCoreAsync(CancellationToken.None).ConfigureAwait(false);
            var cleanup = await RetirePipelineAsync().ConfigureAwait(false);

            _rates.Retire(preservePending: false);
            await TransitionAsync(PlaybackState.Failed, operationToken).ConfigureAwait(false);
            Exception failure = new IOException(reason);
            if (cleanup is not null)
                failure = new AggregateException("网络恢复失败，管线清理也发生错误。", failure, cleanup);
            NotifyFault(CreateError(PlaybackError.PlayFailed, "网络播放恢复失败，已达到重试上限或关闭了重连。"), failure);
        }
        catch (OperationCanceledException) when (operationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (gateEntered && !IsDisposed && !operationToken.IsCancellationRequested)
            {
                _rates.Retire(preservePending: false);
                await TransitionAsync(PlaybackState.Failed).ConfigureAwait(false);
                NotifyFault(CreateError(PlaybackError.PlayFailed, "网络播放恢复失败。"), ex);
            }
        }
        finally
        {
            if (gateEntered)
                _pipelineMutationGate.Release();
        }
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
        var code = playbackError switch
        {
            PlaybackError.OpenFailed => FluxMediaErrorCode.MediaOpenFailed,
            PlaybackError.RecordingFailed => FluxMediaErrorCode.RecordingFailed,
            PlaybackError.ResourceDisposed => FluxMediaErrorCode.ResourceDisposed,
            _ => FluxMediaErrorCode.InvalidState
        };
        return new FluxMediaError(code, message, ComponentName, InstanceId, State.ToString(), CurrentSource?.Uri);
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
    /// 返回控制失败结果，由调用方决定如何提示。
    /// </summary>
    private static PlaybackResult ControlFailure(PlaybackError error, string message)
    {
        return PlaybackResult.Failure(error, message);
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
    /// 创建新一代管线，旧代通知不会再写入当前会话。
    /// </summary>
    private PlaybackPipeline CreatePipeline(TimeSpan position)
    {
        PlaybackPipeline? pipeline = null;
        var callbacks = new PlaybackPipelineCallbacks(() => _isPaused, () => PlaybackRate, () => _volume, () => _isMuted,
            _resyncSignal.Snapshot, () => Volatile.Read(ref _videoOutputCapabilities),
            frame => ReferenceEquals(_pipeline, pipeline) ? PublishVideoFrameAsync(frame) : default);
        pipeline = new PlaybackPipeline(CurrentSource!, _currentOptions, InstanceId, position, _videoFrames, callbacks, _operationCts!.Token, _rates);
        _pipeline = pipeline;
        return pipeline;
    }
    /// <summary>
    /// 启动管线并独立观察其完成状态。
    /// </summary>
    private void StartPipeline(PlaybackPipeline pipeline)
    {
        pipeline.Start();
        _ = ObservePipelineAsync(pipeline);
    }
    /// <summary>
    /// 退役并收集清理错误，由调用方完成状态迁移后反馈。
    /// </summary>
    private async Task<Exception?> RetirePipelineAsync()
    {
        var pipeline = _pipeline;
        _pipeline = null;
        if (pipeline is null)
            return null;
        _rates.Retire(preservePending: true);
        var errors = new List<Exception>();
        try
        {
            await pipeline.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception) { errors.Add(exception); }
        try
        {
            _videoFrames.ClearPublicOutput();
        }
        catch (Exception exception) { errors.Add(exception); }
        if (errors.Count == 0)
            return null;
        var failure = new AggregateException("播放管线退役时发生资源清理错误。", errors);

        return failure;
    }

    /// <summary>
    /// 只由当前代触发完成、失败和恢复，通知与公开控制共享一把锁。
    /// </summary>
    private async Task ObservePipelineAsync(PlaybackPipeline pipeline)
    {
        Exception? failure = null;
        try
        {
            var completion = pipeline.Completion!;
            await completion.ConfigureAwait(false);
        }
        catch (Exception exception) { failure = exception; }
        if (!ReferenceEquals(_pipeline, pipeline) || _operationCts?.IsCancellationRequested != false || IsDisposed)
            return;
        if (failure is OperationCanceledException && pipeline.IsCanceled)
            return;
        if (failure is not null && !pipeline.HasControlFailure && CurrentSource?.Kind == MediaSourceKind.NetworkStream && pipeline.Demuxer.HasFailed)
        {
            await TryRecoverNetworkPlaybackAsync(pipeline, failure.Message).ConfigureAwait(false);
            return;
        }
        await _pipelineMutationGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!ReferenceEquals(_pipeline, pipeline) || _operationCts?.IsCancellationRequested != false || IsDisposed)
                return;
            await StopRecordingCoreAsync(CancellationToken.None).ConfigureAwait(false);
            if (failure is null)
            {
                await TransitionAsync(PlaybackState.Completed).ConfigureAwait(false);
            }
            else
            {
                var cleanup = await RetirePipelineAsync().ConfigureAwait(false);
                if (cleanup is not null)
                    failure = new AggregateException("播放失败，管线清理也发生错误。", failure, cleanup);
                if (State != PlaybackState.Failed)
                {
                    _rates.Retire(preservePending: false);
                    await TransitionAsync(PlaybackState.Failed).ConfigureAwait(false);
                    NotifyFault(CreateError(PlaybackError.PlayFailed, $"播放失败：{failure.Message}"), failure);
                }
            }
        }
        finally { _pipelineMutationGate.Release(); }
    }

    /// <summary>
    /// 独立管理跳转请求合并。
    /// </summary>
    private readonly PlaybackSeekController _seeking;

    /// <summary>
    /// 跳转执行与打开、播放、停止、重连共用管线锁。
    /// </summary>
    private async Task<PlaybackResult> ExecuteQueuedSeekAsync(TimeSpan target, bool resumePlaying, CancellationToken cancellationToken)
    {
        var completion = _rates.Suspend(discardPending: true);
        try
        {
            await WaitForRateBarrierAsync(completion, cancellationToken).ConfigureAwait(false);
            await _pipelineMutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return await ExecuteSeekAsync(target, resumePlaying, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _pipelineMutationGate.Release();
            }
        }
        finally
        {
            _rates.Resume();
        }
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
        if (!_recording.HasRecording)
            return PlaybackResult.Success();
        var result = await _recording.StopAsync(Demuxer, cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            NotifyFault(CreateError(result.Error, result.Message ?? "录制收尾失败。"));
        }
        return result;
    }
}
