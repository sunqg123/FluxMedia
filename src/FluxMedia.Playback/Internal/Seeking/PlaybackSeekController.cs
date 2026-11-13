namespace FluxMedia.Playback.Internal;

/// <summary>
/// 合并跳转请求，并独立维护执行与自适应策略。
/// </summary>
internal sealed class PlaybackSeekController : IAsyncDisposable
{
    /// <summary>
    /// 跳转合并延迟。
    /// </summary>
    private static readonly TimeSpan SeekCoalesceDelay = TimeSpan.FromMilliseconds(33);

    /// <summary>
    /// 最小跳转合并延迟。
    /// </summary>
    private static readonly TimeSpan MinSeekCoalesceDelay = TimeSpan.FromMilliseconds(16);

    /// <summary>
    /// 最大跳转合并延迟。
    /// </summary>
    private static readonly TimeSpan MaxSeekCoalesceDelay = TimeSpan.FromMilliseconds(66);

    /// <summary>
    /// 慢速解复用跳转阈值。
    /// </summary>
    private static readonly TimeSpan SlowDemuxSeekThreshold = TimeSpan.FromMilliseconds(350);

    /// <summary>
    /// 慢速首次界面跳转阈值。
    /// </summary>
    private static readonly TimeSpan SlowFirstUiSeekThreshold = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// 当前跳转合并等待时间（时钟刻度）。
    /// </summary>
    private long _adaptiveSeekCoalesceDelayTicks = SeekCoalesceDelay.Ticks;

    /// <summary>
    /// 保护待执行请求及处理任务。
    /// </summary>
    private readonly object _sync = new();
    /// <summary>
    /// 会话提供的实际跳转动作。
    /// </summary>
    private readonly Func<TimeSpan, bool, CancellationToken, Task<PlaybackResult>> _execute;
    /// <summary>
    /// 当前等待合并的请求。
    /// </summary>
    private SeekRequest? _pending;
    /// <summary>
    /// 合并请求共享的完成通知。
    /// </summary>
    private TaskCompletionSource<PlaybackResult>? _pendingCompletion;
    /// <summary>
    /// 唯一的后台调度任务。
    /// </summary>
    private Task? _processing;
    /// <summary>
    /// 执行中的目标与后续排队目标隔离。
    /// </summary>
    private bool _executing;
    /// <summary>
    /// 请求递增标识。
    /// </summary>
    private long _nextId;
    /// <summary>
    /// 是否已经释放。
    /// </summary>
    private bool _disposed;
    /// <summary>
    /// 当前执行与合并策略所需的状态。
    /// </summary>
    private PlaybackSeekState _operation = new();

    /// <summary>
    /// 在请求锁内读取当前执行状态。
    /// </summary>
    public PlaybackSeekState Operation { get { lock (_sync) return _operation; } }
    /// <summary>
    /// 下一次合并等待时间。
    /// </summary>
    public TimeSpan CoalesceDelay => GetAdaptiveSeekCoalesceDelay();

    /// <summary>
    /// 绑定实际跳转动作。
    /// </summary>
    public PlaybackSeekController(Func<TimeSpan, bool, CancellationToken, Task<PlaybackResult>> execute)
    {
        _execute = execute;
    }
    /// <summary>
    /// 合并目标，并在入队时固定操作令牌，防止旧请求跨媒体执行。
    /// </summary>
    public Task<PlaybackResult> Enqueue(TimeSpan target, bool resumePlaying, CancellationToken operationToken)
    {
        operationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(PlaybackSeekController));
            var request = new SeekRequest(target, resumePlaying, ++_nextId, _pending?.FirstQueuedAt ?? DateTimeOffset.UtcNow, (_pending?.Count ?? 0) + 1, operationToken);
            _pending = request;
            _pendingCompletion ??= new TaskCompletionSource<PlaybackResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            var completion = _pendingCompletion.Task;
            if (!_executing)
            {
                _operation = new PlaybackSeekState
                {
                    RequestId = request.Id,
                    CoalescedRequestCount = request.Count,
                };
            }
            _processing ??= Task.Run(ProcessAsync);
            return completion;
        }
    }
    /// <summary>
    /// 每次只执行一组最新目标，执行中的请求使用原操作令牌。
    /// </summary>
    private async Task ProcessAsync()
    {
        DateTimeOffset? lastStartedAt = null;
        while (true)
        {
            // 执行本身已经占用合并间隔时，直接处理最新目标。
            var delay = lastStartedAt.HasValue
                ? CoalesceDelay - (DateTimeOffset.UtcNow - lastStartedAt.Value)
                : CoalesceDelay;
            if (delay > TimeSpan.Zero) await Task.Delay(delay).ConfigureAwait(false);
            SeekRequest request;
            TaskCompletionSource<PlaybackResult> completion;
            lock (_sync)
            {
                if (_pending is null) { _processing = null; return; }
                request = _pending;
                completion = _pendingCompletion!;
                _pending = null;
                _pendingCompletion = null;
                _executing = true;
                _operation = new PlaybackSeekState
                {
                    RequestId = request.Id,
                    CoalescedRequestCount = request.Count,
                };
            }
            lastStartedAt = DateTimeOffset.UtcNow;
            try
            {
                request.Token.ThrowIfCancellationRequested();
                completion.TrySetResult(await _execute(request.Target, request.ResumePlaying, request.Token).ConfigureAwait(false));
            }
            catch (OperationCanceledException) { completion.TrySetCanceled(); }
            catch (Exception exception) { completion.TrySetException(exception); }
            finally
            {
                lock (_sync) _executing = false;
            }
        }
    }
    /// <summary>
    /// 取消尚未执行的请求；执行中的动作由其操作令牌取消。
    /// </summary>
    public void CancelPending(Exception exception)
    {
        lock (_sync)
        {
            if (exception is OperationCanceledException) _pendingCompletion?.TrySetCanceled();
            else _pendingCompletion?.TrySetException(exception);
            _pendingCompletion = null;
            _pending = null;
        }
    }

    /// <summary>
    /// 新媒体重新开始请求合并。
    /// </summary>
    public void Reset()
    {
        lock (_sync)
        {
            CancelPending(new OperationCanceledException());
            _operation = new PlaybackSeekState();
            Interlocked.Exchange(ref _adaptiveSeekCoalesceDelayTicks, SeekCoalesceDelay.Ticks);
        }
    }
    /// <summary>
    /// 等待调度线程退出，不再接受请求。
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        Task? processing;
        lock (_sync)
        {
            _disposed = true;
            CancelPending(new OperationCanceledException());
            processing = _processing;
        }
        if (processing is not null) await processing.ConfigureAwait(false);
    }

    /// <summary>
    /// 开始执行当前请求，重置恢复和调度依据。
    /// </summary>
    public void Begin()
    {
        lock (_sync)
            _operation = _operation with
            {
                Path = PlaybackSeekPath.None,
                DemuxerSeekElapsed = null,
                TotalElapsed = null,
                FirstFrameElapsed = null,
                RecoveryAttempted = false
            };
    }

    /// <summary>
    /// 保存实际执行路径和自适应策略需要的定位耗时。
    /// </summary>
    public void SetPath(PlaybackSeekPath path, TimeSpan? elapsed)
    {
        lock (_sync) _operation = _operation with { Path = path, DemuxerSeekElapsed = elapsed };
    }

    /// <summary>
    /// 只接收当前请求的首帧，避免覆盖后续请求。
    /// </summary>
    public void RecordFirstFrame(long requestId, TimeSpan elapsed)
    {
        lock (_sync)
            if (_operation.RequestId == requestId)
                _operation = _operation with { FirstFrameElapsed = elapsed };
    }

    /// <summary>
    /// 记录首帧恢复，供后续合并策略退避。
    /// </summary>
    public void MarkRecoveryAttempted()
    {
        lock (_sync) _operation = _operation with { RecoveryAttempted = true };
    }

    /// <summary>
    /// 使用本次执行耗时更新下一次合并间隔。
    /// </summary>
    public void Complete(TimeSpan elapsed)
    {
        lock (_sync)
        {
            _operation = _operation with { TotalElapsed = elapsed };
            UpdateStrategy(_operation);
        }
    }

    /// <summary>
    /// 读取当前自适应跳转合并间隔。
    /// </summary>
    private TimeSpan GetAdaptiveSeekCoalesceDelay()
    {
        return TimeSpan.FromTicks(Interlocked.Read(ref _adaptiveSeekCoalesceDelayTicks));
    }

    /// <summary>
    /// 根据跳转耗时调整后续合并策略。
    /// </summary>
    private void UpdateStrategy(PlaybackSeekState seek)
    {
        var next = GetAdaptiveSeekCoalesceDelay();
        var slowDemux = seek.DemuxerSeekElapsed >= SlowDemuxSeekThreshold;
        var slowFirstUi = seek.FirstFrameElapsed >= SlowFirstUiSeekThreshold;
        var heavyDrag = seek.CoalescedRequestCount >= 4;
        var fullRestart = seek.Path == PlaybackSeekPath.FullRestart;
        var recovered = seek.RecoveryAttempted;

        if (heavyDrag)
        {
            next = MinSeekCoalesceDelay;
        }
        else if (slowDemux || slowFirstUi || fullRestart || recovered)
        {
            next = TimeSpan.FromMilliseconds(Math.Min(MaxSeekCoalesceDelay.TotalMilliseconds, next.TotalMilliseconds + 20));
        }
        else if (seek.Path == PlaybackSeekPath.Demuxer && seek.CoalescedRequestCount <= 1 && seek.TotalElapsed <= TimeSpan.FromMilliseconds(180))
        {
            next = TimeSpan.FromMilliseconds(Math.Max(MinSeekCoalesceDelay.TotalMilliseconds, next.TotalMilliseconds - 10));
        }

        Interlocked.Exchange(ref _adaptiveSeekCoalesceDelayTicks, next.Ticks);
    }

}
