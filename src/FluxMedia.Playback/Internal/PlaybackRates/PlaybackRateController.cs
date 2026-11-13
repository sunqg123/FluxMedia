using LogConverge.Sharp;

namespace FluxMedia.Playback.Internal;

/// <summary>
/// 协调换速准备、提交和生命周期，不持有原生滤镜。
/// </summary>
internal sealed class PlaybackRateController
{
    /// <summary>
    /// 只保护短状态变更，不等待工作线程。
    /// </summary>
    private readonly object _sync = new();
    /// <summary>
    /// 所属播放器。
    /// </summary>
    private readonly Guid _instanceId;
    /// <summary>
    /// 读取实际暂停状态。
    /// </summary>
    private readonly Func<bool> _isPaused;
    /// <summary>
    /// 启动时确定的倍速范围与自动推算策略。
    /// </summary>
    private readonly FluxEngineOptions _engineOptions;
    /// <summary>
    /// 当前媒体的换速超时配置。
    /// </summary>
    private PlaybackOptions _options = new();
    /// <summary>
    /// 原子发布的换速请求结果与实际倍速。
    /// </summary>
    private PlaybackRateSnapshot _snapshot = new();
    /// <summary>
    /// 可被新目标取代的待处理请求。
    /// </summary>
    private PlaybackRateRequest? _pending;
    /// <summary>
    /// 已取得提交资格的请求，不再接受普通取消。
    /// </summary>
    private PlaybackRateRequest? _committing;
    /// <summary>
    /// 唤醒等待数据包的音频线程。
    /// </summary>
    private TaskCompletionSource<bool> _changed = NewSignal();
    /// <summary>
    /// 请求编号。
    /// </summary>
    private long _requestId;
    /// <summary>
    /// 管线代次。
    /// </summary>
    private long _generation;
    /// <summary>
    /// 解码同步代次。
    /// </summary>
    private long _resyncVersion;
    /// <summary>
    /// 暂停、Seek 等持有的提交屏障数量。
    /// </summary>
    private int _suspensions;
    /// <summary>
    /// 当前媒体是否需要音频处理链。
    /// </summary>
    private bool _hasAudio = true;
    /// <summary>
    /// 音频线程是否已启动。
    /// </summary>
    private bool _audioStarted;
    /// <summary>
    /// 基础音频图是否已就绪。
    /// </summary>
    private bool _audioReady;
    /// <summary>
    /// 音频是否已正常完成。
    /// </summary>
    private bool _audioEnded;

    /// <summary>
    /// 绑定启动策略、会话标识和暂停快照。
    /// </summary>
    public PlaybackRateController(FluxEngineOptions engineOptions, Guid instanceId, Func<bool> isPaused)
    {
        _engineOptions = engineOptions;
        _instanceId = instanceId;
        _isPaused = isPaused;
    }

    /// <summary>
    /// 实际速度或待应用状态变化。
    /// </summary>
    public event Action? Changed;
    /// <summary>
    /// 提交无法完成，要求对应管线进入故障路径。
    /// </summary>
    public event Action<long, Exception>? CommitFaulted;
    /// <summary>
    /// 一次读取一致的倍速状态。
    /// </summary>
    public PlaybackRateSnapshot Snapshot => Volatile.Read(ref _snapshot);
    /// <summary>
    /// 音频建图沿用上下限推算的最大级数。
    /// </summary>
    public int MaxAudioTempoStages => _engineOptions.MaxAudioTempoStages;
    /// <summary>
    /// 当前控制唤醒任务，不消费数据包。
    /// </summary>
    public Task ChangeSignal
    {
        get
        {
            lock (_sync)
                return _changed.Task;
        }
    }

    /// <summary>
    /// 为新媒体清理请求并校验配置。
    /// </summary>
    public void Reset(PlaybackOptions options)
    {
        ValidateOptions(options);
        lock (_sync)
        {
            RejectAllLocked("媒体已切换。", PlaybackError.OperationCanceled);
            _options = options;
            _generation++;
            _hasAudio = true;
            _audioStarted = _audioReady = _audioEnded = false;
            Volatile.Write(ref _snapshot, new PlaybackRateSnapshot
            {
                MinRate = _engineOptions.MinPlaybackRate,
                MaxRate = _engineOptions.MaxPlaybackRate
            });
            SignalLocked();
        }
        Notify();
    }

    /// <summary>
    /// 新管线接管同一媒体的最新待处理请求。
    /// </summary>
    public long BindPipeline(long resyncVersion)
    {
        long generation;
        lock (_sync)
        {
            generation = ++_generation;
            _resyncVersion = resyncVersion;
            _audioStarted = _audioReady = _audioEnded = false;
            SignalLocked();
        }
        return generation;
    }

    /// <summary>
    /// 打开后确定是否存在音轨，纯视频直接采用速度。
    /// </summary>
    public void SetAudioTrack(long generation, bool hasAudio)
    {
        lock (_sync)
        {
            if (generation != _generation)
                return;
            _hasAudio = hasAudio;
            ApplyWithoutAudioLocked();
            RefreshLocked();
            SignalLocked();
        }
        Notify();
    }

    /// <summary>
    /// 标记音频启动阶段，防止把初始化误认为没有工作器。
    /// </summary>
    public void AudioStarting(long generation)
    {
        lock (_sync)
        {
            if (generation == _generation)
                _audioStarted = true;
        }
    }

    /// <summary>
    /// 基础图建立后允许处理独立换速请求。
    /// </summary>
    public void AudioReady(long generation)
    {
        lock (_sync)
        {
            if (generation != _generation)
                return;
            _audioReady = true;
            SignalLocked();
        }
        Notify();
    }

    /// <summary>
    /// 正常音频结束后由视频直接采用速度；故障不得转换成成功。
    /// </summary>
    public void AudioExited(long generation, bool completed, bool faulted)
    {
        lock (_sync)
        {
            if (generation != _generation)
                return;
            _audioReady = false;
            _audioStarted = false;
            _audioEnded = completed;
            var committingRequestId = _committing?.Id;
            var failedRequestId = faulted ? _pending?.Id ?? committingRequestId : committingRequestId;
            if (completed)
                ApplyWithoutAudioLocked();
            else if (faulted)
                RejectAllLocked("音频工作器已失败。", PlaybackError.PlayFailed);
            else if (_committing is not null)
            {
                FinishLocked(_committing, PlaybackResult.Failure(PlaybackError.OperationCanceled, "提交所属音频工作器已退出。"));
                _committing = null;
            }
            var exitState = faulted && failedRequestId.HasValue ? "Rejected"
                : !completed && committingRequestId.HasValue ? "Retired" : null;
            var exitError = faulted && failedRequestId.HasValue ? "音频工作器已失败。"
                : !completed && committingRequestId.HasValue ? "提交所属音频工作器已退出。" : null;
            RefreshLocked(exitState, exitError, requestId: failedRequestId);
            SignalLocked();
        }
        Notify();
    }

    /// <summary>
    /// 登记目标并唤醒音频，等待者由会话在锁外等待。
    /// </summary>
    public PlaybackRateRequest Submit(double rate, bool defer, bool canChangeSpeed = true, string? capabilityError = null)
    {
        PlaybackRateRequest request;
        lock (_sync)
        {
            var valid = PlaybackRatePolicy.TryValidate(rate, _engineOptions, _hasAudio, out var stages, out var error);
            if (valid && !canChangeSpeed)
            {
                valid = false;
                error = capabilityError ?? "当前媒体不支持倍速播放。";
            }
            request = new PlaybackRateRequest(++_requestId, rate, stages);
            Volatile.Write(ref _snapshot, _snapshot with
            {
                RequestId = request.Id,
                RequestedRate = rate,
                LastError = null
            });
            if (!valid)
            {
                var failure = PlaybackResult.Failure(PlaybackError.PlaybackRateNotSupported, error!);
                FinishLocked(request, failure);
                RefreshLocked("Rejected", failure.Message);
            }
            else
            {
                if (_pending is not null)
                    FinishLocked(_pending, PlaybackResult.Failure(PlaybackError.OperationCanceled, "倍速请求已被新目标取代。"));
                _pending = request;
                if (_committing is null && rate == _snapshot.AppliedRate)
                    ApplyPendingLocked();
                else if ((!_hasAudio || _audioEnded) && _suspensions == 0 && _committing is null)
                    ApplyPendingLocked();
                else if (defer || !_audioStarted || _suspensions != 0 || _isPaused())
                    DeferLocked(request);
                RefreshLocked();
            }
            SignalLocked();
        }
        Notify();
        return request;
    }

    /// <summary>
    /// 调用超时和取消只能作废尚未提交、未返回 Accepted 的请求。
    /// </summary>
    public async ValueTask<PlaybackResult> WaitAsync(PlaybackRateRequest request, CancellationToken cancellationToken)
    {
        // Accepted 已经完成调用确认，不为暂停等待建立超时计时器。
        if (request.Acknowledgement.Task.IsCompleted)
            return await request.Acknowledgement.Task.ConfigureAwait(false);
        using var timeout = new CancellationTokenSource();
        using var registration = cancellationToken.Register(() => TryAbandon(request, "倍速请求已取消。"));
        var delay = Task.Delay(_options.PlaybackRateTimeout, timeout.Token);
        try
        {
            if (await Task.WhenAny(request.Acknowledgement.Task, delay).ConfigureAwait(false) == delay)
                TryAbandon(request, "倍速准备超时。");
            return await request.Acknowledgement.Task.ConfigureAwait(false);
        }
        finally { timeout.Cancel(); }
    }

    /// <summary>
    /// 音频只读取请求，不以已发布速度的变化作为触发源。
    /// </summary>
    public PlaybackRateRequest? Peek(long generation, long resyncVersion)
    {
        lock (_sync)
            return CanCommitLocked(generation, resyncVersion) ? _pending : null;
    }

    /// <summary>
    /// 原子取得提交资格，后续完成路径不获取管线锁。
    /// </summary>
    public bool TryBeginCommit(PlaybackRateRequest request, long generation, long resyncVersion)
    {
        lock (_sync)
        {
            if (!CanCommitLocked(generation, resyncVersion) || !ReferenceEquals(_pending, request))
                return false;
            _pending = null;
            _committing = request;
            request.State = PlaybackRateRequestState.Committing;
            RefreshLocked();
        }
        Notify();
        _ = WatchCommitAsync(request, generation);
        return true;
    }

    /// <summary>
    /// 图替换完成后才发布实际速度，旧管线结果直接忽略。
    /// </summary>
    public void Applied(PlaybackRateRequest request, long generation, long resyncVersion)
    {
        lock (_sync)
        {
            if (generation != _generation || resyncVersion != _resyncVersion || !ReferenceEquals(_committing, request))
                return;
            _committing = null;
            var sameTarget = _pending?.Rate == request.Rate ? _pending : null;
            if (sameTarget is not null)
                _pending = null;
            // 先原子发布完整快照，再放行等待者。
            RefreshLocked("Applied", appliedRate: request.Rate,
                requestId: sameTarget?.Id ?? request.Id, clearError: true);
            FinishLocked(request, PlaybackResult.Success());
            if (sameTarget is not null)
                FinishLocked(sameTarget, PlaybackResult.Success());
            SignalLocked();
        }
        Notify();
    }

    /// <summary>
    /// 只有当前候选图构建失败才拒绝请求，不破坏旧链。
    /// </summary>
    public void BuildFailed(PlaybackRateRequest request, long generation, long resyncVersion, Exception error)
    {
        lock (_sync)
        {
            if (generation != _generation || resyncVersion != _resyncVersion || !ReferenceEquals(_pending, request))
                return;
            _pending = null;
            FinishLocked(request, PlaybackResult.Failure(PlaybackError.PlaybackRateNotSupported, error.Message));
            RefreshLocked("Rejected", error.Message, requestId: request.Id);
            SignalLocked();
        }
        Log.Write(LogLevel.Warning, nameof(PlaybackRateController),
            $"SessionId={_instanceId} Event=RateBuildRejected RequestId={request.Id} Rate={request.Rate}", error);
        Notify();
    }

    /// <summary>
    /// 阻止后续提交，调用方在管线锁外等待已有提交。
    /// </summary>
    public Task<PlaybackResult> Suspend(bool discardPending)
    {
        Task<PlaybackResult> completion;
        string? rejection = null;
        long? rejectedRequestId = null;
        lock (_sync)
        {
            _suspensions++;
            if (_pending is not null)
            {
                if (discardPending)
                {
                    FinishLocked(_pending, PlaybackResult.Failure(PlaybackError.OperationCanceled, "Seek 已作废未提交的倍速请求。"));
                    rejection = "Seek 已作废未提交的倍速请求。";
                    rejectedRequestId = _pending.Id;
                    _pending = null;
                }
                else
                    DeferLocked(_pending);
            }
            RefreshLocked(rejection is null ? null : "Abandoned", rejection, requestId: rejectedRequestId);
            completion = _committing?.Completion.Task ?? Task.FromResult(PlaybackResult.Success());
            SignalLocked();
        }
        Notify();
        return completion;
    }

    /// <summary>
    /// 生命周期动作完成后重新允许提交。
    /// </summary>
    public void Resume()
    {
        lock (_sync)
        {
            if (_suspensions > 0)
                _suspensions--;
            ApplyWithoutAudioLocked();
            RefreshLocked();
            SignalLocked();
        }
        Notify();
    }

    /// <summary>
    /// 原地 Seek 更换同步代次，拒绝旧位置的候选图。
    /// </summary>
    public void Resync(long version)
    {
        lock (_sync)
        {
            _resyncVersion = version;
            SignalLocked();
        }
    }

    /// <summary>
    /// 恢复、请求或状态变化时唤醒音频线程。
    /// </summary>
    public void Wake()
    {
        lock (_sync)
            SignalLocked();
    }

    /// <summary>
    /// 退役时阻断旧图发布，同媒体重建可保留待处理请求。
    /// </summary>
    public void Retire(bool preservePending)
    {
        lock (_sync)
        {
            var interrupted = _committing is not null || (!preservePending && _pending is not null);
            var retiredRequestId = !preservePending && _pending is not null ? _pending.Id : _committing?.Id;
            _generation++;
            _audioStarted = _audioReady = _audioEnded = false;
            if (_committing is not null)
                FinishLocked(_committing, PlaybackResult.Failure(PlaybackError.OperationCanceled, "提交所属管线已退役。"));
            _committing = null;
            if (!preservePending && _pending is not null)
            {
                FinishLocked(_pending, PlaybackResult.Failure(PlaybackError.OperationCanceled, "播放操作已结束。"));
                _pending = null;
            }
            RefreshLocked(interrupted ? "Retired" : null, interrupted ? "倍速请求所属播放操作已结束。" : null,
                requestId: retiredRequestId);
            SignalLocked();
        }
        Notify();
    }

    /// <summary>
    /// 提交超时只能转为管线故障，不能恢复已经排空的旧图。
    /// </summary>
    private async Task WatchCommitAsync(PlaybackRateRequest request, long generation)
    {
        using var timer = new CancellationTokenSource();
        var delay = Task.Delay(_options.PlaybackRateCommitTimeout, timer.Token);
        if (await Task.WhenAny(request.Completion.Task, delay).ConfigureAwait(false) != delay)
        {
            timer.Cancel();
            return;
        }
        FailCommit(request, generation, new TimeoutException("音频倍速提交超时，当前管线必须停止。"), "RateCommitWatchdog");
    }

    /// <summary>
    /// 排尾音或替换失败属于不可恢复的提交故障。
    /// </summary>
    public void CommitFailed(PlaybackRateRequest request, long generation, Exception error) =>
        FailCommit(request, generation, error, "RateCommitFailed");

    /// <summary>
    /// 原子结束故障提交，再在短锁外取消对应管线。
    /// </summary>
    private void FailCommit(PlaybackRateRequest request, long generation, Exception error, string eventName)
    {
        lock (_sync)
        {
            if (generation != _generation || !ReferenceEquals(_committing, request))
                return;
            var failedRequestId = _pending?.Id ?? request.Id;
            RejectAllLocked(error.Message, PlaybackError.PlayFailed);
            RefreshLocked("Faulted", error.Message, requestId: failedRequestId);
            SignalLocked();
        }
        Log.Error(nameof(PlaybackRateController),
            $"SessionId={_instanceId} Event={eventName} RequestId={request.Id} Generation={generation}", error);
        CommitFaulted?.Invoke(generation, error);
        Notify();
    }

    /// <summary>
    /// 在普通取消与提交之间确定唯一赢家。
    /// </summary>
    private void TryAbandon(PlaybackRateRequest request, string reason)
    {
        lock (_sync)
        {
            if (request.Deferred || !ReferenceEquals(_pending, request))
                return;
            _pending = null;
            FinishLocked(request, PlaybackResult.Failure(PlaybackError.OperationCanceled, reason));
            RefreshLocked("Abandoned", reason, requestId: request.Id);
            SignalLocked();
        }
        Notify();
    }

    /// <summary>
    /// 提交同时校验请求、管线、同步代次和生命周期屏障。
    /// </summary>
    private bool CanCommitLocked(long generation, long resyncVersion) => generation == _generation
        && resyncVersion == _resyncVersion && _pending is { State: PlaybackRateRequestState.Pending } && _committing is null
        && _audioReady && _suspensions == 0 && !_isPaused();

    /// <summary>
    /// 暂停和未启动时只接受目标，不完成内部最终结果。
    /// </summary>
    private static void DeferLocked(PlaybackRateRequest request)
    {
        request.Deferred = true;
        request.Acknowledgement.TrySetResult(PlaybackResult.Accepted());
    }

    /// <summary>
    /// 无音频链时直接应用待处理目标。
    /// </summary>
    private void ApplyWithoutAudioLocked()
    {
        if ((!_hasAudio || _audioEnded) && _suspensions == 0 && _committing is null)
            ApplyPendingLocked();
    }

    /// <summary>
    /// 发布无需建图的目标。
    /// </summary>
    private void ApplyPendingLocked()
    {
        if (_pending is null)
            return;
        var request = _pending;
        _pending = null;
        RefreshLocked("Applied", appliedRate: request.Rate, requestId: request.Id, clearError: true);
        FinishLocked(request, PlaybackResult.Success());
    }

    /// <summary>
    /// 一次完成调用确认与内部最终结果。
    /// </summary>
    private static void FinishLocked(PlaybackRateRequest request, PlaybackResult result)
    {
        request.State = result.IsSuccess ? PlaybackRateRequestState.Applied : PlaybackRateRequestState.Rejected;
        request.Acknowledgement.TrySetResult(result);
        request.Completion.TrySetResult(result);
    }

    /// <summary>
    /// 结束所有请求，保证等待者都有结果。
    /// </summary>
    private void RejectAllLocked(string reason, PlaybackError error)
    {
        var result = PlaybackResult.Failure(error, reason);
        if (_pending is not null)
            FinishLocked(_pending, result);
        if (_committing is not null)
            FinishLocked(_committing, result);
        _pending = _committing = null;
    }

    /// <summary>
    /// 按请求编号更新最近结果，旧请求不能覆盖新请求的错误。
    /// </summary>
    private void RefreshLocked(string? state = null, string? error = null, double? appliedRate = null,
        long? requestId = null, bool clearError = false)
    {
        var affectsLatest = requestId is null || requestId == _snapshot.RequestId;
        var currentState = _snapshot.State;
        if (_pending is { } pending && pending.Id == _snapshot.RequestId)
            currentState = pending.Deferred ? "Accepted" : "Pending";
        else if (_committing is { } committing && committing.Id == _snapshot.RequestId)
            currentState = "Committing";
        Volatile.Write(ref _snapshot, _snapshot with
        {
            AppliedRate = appliedRate ?? _snapshot.AppliedRate,
            Pending = _pending is not null || _committing is not null,
            State = affectsLatest && state is not null ? state : currentState,
            LastError = affectsLatest ? (clearError ? null : error ?? _snapshot.LastError) : _snapshot.LastError
        });
    }

    /// <summary>
    /// 替换广播信号，未完成的读包等待可复用。
    /// </summary>
    private void SignalLocked()
    {
        var previous = _changed;
        _changed = NewSignal();
        previous.TrySetResult(true);
    }

    /// <summary>
    /// 创建不会同步调用续体的广播信号。
    /// </summary>
    private static TaskCompletionSource<bool> NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// 在短锁外通知界面，订阅者错误不影响音频提交。
    /// </summary>
    private void Notify()
    {
        try
        {
            Changed?.Invoke();
        }
        catch (Exception error)
        {
            Log.Write(LogLevel.Warning, nameof(PlaybackRateController),
                $"SessionId={_instanceId} Event=RateNotificationFailed", error);
        }
    }

    /// <summary>
    /// 检查异步计时器支持的时间范围。
    /// </summary>
    private static void ValidateTimeout(TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero || timeout.TotalMilliseconds > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(timeout));
    }

    /// <summary>
    /// 在改变媒体状态前检查计时器参数。
    /// </summary>
    public static void ValidateOptions(PlaybackOptions options)
    {
        ValidateTimeout(options.PlaybackRateTimeout);
        ValidateTimeout(options.PlaybackRateCommitTimeout);
    }
}
