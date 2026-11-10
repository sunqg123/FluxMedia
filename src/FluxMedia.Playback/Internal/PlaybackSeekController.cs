using FluxMedia.Core.Logging;
using FluxMedia.Core.Configuration;

namespace FluxMedia.Playback.Internal;

/// <summary>
/// 合并跳转请求、绑定请求生命周期，并维护跳转诊断。
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
    /// 跳转跟踪容量。
    /// </summary>
    private const int SeekTraceCapacity = 48;


    /// <summary>
    /// 保护跳转跟踪记录的锁。
    /// </summary>
    private readonly object _seekTraceLock = new();


    /// <summary>
    /// 按时间保存的近期跳转阶段记录。
    /// </summary>
    private readonly Queue<PlaybackSeekTraceEntry> _seekTrace = new();


    /// <summary>
    /// 当前跳转合并等待时间（时钟刻度）。
    /// </summary>
    private long _adaptiveSeekCoalesceDelayTicks = SeekCoalesceDelay.Ticks;


    /// <summary>
    /// 当前自适应跳转合并策略的名称。
    /// </summary>
    private string _seekStrategy = "Balanced";

    /// <summary>
    /// 保护待执行请求及处理任务。
    /// </summary>
    private readonly object _sync = new();
    /// <summary>
    /// 所属播放会话标识。
    /// </summary>
    private readonly Guid _instanceId;
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
    /// 当前跳转诊断快照。
    /// </summary>
    public PlaybackSeekDiagnosticInfo Diagnostic { get; set; } = new();
    /// <summary>
    /// 当前合并策略名称。
    /// </summary>
    public string Strategy => _seekStrategy;
    /// <summary>
    /// 下一次合并等待时间。
    /// </summary>
    public TimeSpan CoalesceDelay => GetAdaptiveSeekCoalesceDelay();

    /// <summary>
    /// 绑定会话标识与实际跳转动作。
    /// </summary>
    public PlaybackSeekController(Guid instanceId, Func<TimeSpan, bool, CancellationToken, Task<PlaybackResult>> execute)
    {
        _instanceId = instanceId;
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
            var request = new SeekRequest(target, resumePlaying, ++_nextId,
                _pending?.FirstQueuedAt ?? DateTimeOffset.UtcNow, (_pending?.Count ?? 0) + 1, operationToken);
            _pending = request;
            _pendingCompletion ??= new TaskCompletionSource<PlaybackResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            var completion = _pendingCompletion.Task;
            if (!_executing)
            {
                Diagnostic = Diagnostic with
                {
                    RequestId = request.Id,
                    RequestedPosition = target,
                    InProgress = true,
                    CoalescedRequestCount = request.Count,
                    Strategy = Strategy,
                    NextCoalesceDelay = CoalesceDelay,
                    StartedAt = request.FirstQueuedAt,
                    CompletedAt = null,
                    TotalElapsed = null,
                    FirstUiFrameElapsed = null,
                    LastError = null
                };
            }
            Trace(request.Id, "Queued", $"Target={target} Coalesced={request.Count}", target);
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
                Diagnostic = Diagnostic with
                {
                    RequestId = request.Id,
                    RequestedPosition = request.Target,
                    InProgress = true,
                    CoalescedRequestCount = request.Count,
                    Strategy = Strategy,
                    NextCoalesceDelay = CoalesceDelay,
                    StartedAt = request.FirstQueuedAt,
                    CompletedAt = null,
                    TotalElapsed = null,
                    FirstUiFrameElapsed = null,
                    LastError = null,
                    CoalesceElapsed = DateTimeOffset.UtcNow - request.FirstQueuedAt
                };
            }
            lastStartedAt = DateTimeOffset.UtcNow;
            try
            {
                request.Token.ThrowIfCancellationRequested();
                Trace(request.Id, "CoalescedExecute", $"Target={request.Target} Count={request.Count}", request.Target, Diagnostic.CoalesceElapsed);
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
            Diagnostic = Diagnostic with { InProgress = false, CompletedAt = DateTimeOffset.UtcNow, LastError = exception.Message };
        }
    }

    /// <summary>
    /// 新媒体使用独立的合并策略和诊断起点。
    /// </summary>
    public void Reset()
    {
        lock (_sync)
        {
            CancelPending(new OperationCanceledException());
            Diagnostic = new PlaybackSeekDiagnosticInfo();
            Interlocked.Exchange(ref _adaptiveSeekCoalesceDelayTicks, SeekCoalesceDelay.Ticks);
            _seekStrategy = "Balanced";
            ClearTrace();
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
    /// 读取当前自适应跳转合并间隔。
    /// </summary>
    private TimeSpan GetAdaptiveSeekCoalesceDelay()
    {
        return TimeSpan.FromTicks(Interlocked.Read(ref _adaptiveSeekCoalesceDelayTicks));
    }


    /// <summary>
    /// 根据跳转耗时调整后续合并策略。
    /// </summary>
    public void UpdateStrategy(PlaybackSeekDiagnosticInfo seek)
    {
        var next = GetAdaptiveSeekCoalesceDelay();
        var strategy = "Balanced";
        var slowDemux = seek.DemuxerSeekElapsed >= SlowDemuxSeekThreshold;
        var slowFirstUi = seek.FirstUiFrameElapsed >= SlowFirstUiSeekThreshold;
        var heavyDrag = seek.CoalescedRequestCount >= 4;
        var fullRestart = string.Equals(seek.Path, "FullRestart", StringComparison.OrdinalIgnoreCase);
        var recovered = seek.FirstUiRecoveryAttempted;

        if (heavyDrag)
        {
            next = MinSeekCoalesceDelay;
            strategy = "DragPreview";
        }
        else if (slowDemux || slowFirstUi || fullRestart || recovered)
        {
            next = TimeSpan.FromMilliseconds(Math.Min(MaxSeekCoalesceDelay.TotalMilliseconds, next.TotalMilliseconds + 20));
            strategy = slowFirstUi ? "FirstUiRecovery" : slowDemux ? "DemuxBackoff" : recovered ? "RecoveryBackoff" : "FullRestartBackoff";
        }
        else if (string.Equals(seek.Path, "Demuxer", StringComparison.OrdinalIgnoreCase) &&
            seek.CoalescedRequestCount <= 1 &&
            seek.TotalElapsed <= TimeSpan.FromMilliseconds(180))
        {
            next = TimeSpan.FromMilliseconds(Math.Max(MinSeekCoalesceDelay.TotalMilliseconds, next.TotalMilliseconds - 10));
            strategy = "Responsive";
        }

        Interlocked.Exchange(ref _adaptiveSeekCoalesceDelayTicks, next.Ticks);
        _seekStrategy = strategy;
        Diagnostic = Diagnostic with
        {
            Strategy = strategy,
            NextCoalesceDelay = next
        };
        Trace(seek.RequestId, "Strategy", $"Strategy={strategy} NextCoalesce={next}", seek.ActualPosition);
        FluxMediaLog.Write(LogLevel.Information, nameof(PlaybackSeekController), _instanceId, $"Event=SeekStrategyUpdated Strategy={strategy} NextCoalesce={next} Path={seek.Path ?? "-"} Coalesced={seek.CoalescedRequestCount} Demux={seek.DemuxerSeekElapsed?.ToString() ?? "-"} FirstUI={seek.FirstUiFrameElapsed?.ToString() ?? "-"} Total={seek.TotalElapsed?.ToString() ?? "-"}");
    }


    /// <summary>
    /// 追加一条跳转阶段跟踪记录。
    /// </summary>
    public void Trace(long requestId, string phase, string message, TimeSpan? position = null, TimeSpan? elapsed = null)
    {
        var entry = new PlaybackSeekTraceEntry(DateTimeOffset.UtcNow, requestId, phase, message, position, elapsed);
        lock (_seekTraceLock)
        {
            _seekTrace.Enqueue(entry);
            while (_seekTrace.Count > SeekTraceCapacity)
            {
                _seekTrace.Dequeue();
            }
        }

        FluxMediaLog.Write(LogLevel.Information, nameof(PlaybackSeekController), _instanceId, $"Event=SeekTrace RequestId={requestId} Phase={phase} Position={position?.ToString() ?? "-"} Elapsed={elapsed?.ToString() ?? "-"} Message={message}");
    }


    /// <summary>
    /// 复制当前跳转跟踪记录供诊断读取。
    /// </summary>
    public IReadOnlyList<PlaybackSeekTraceEntry> Snapshot()
    {
        lock (_seekTraceLock)
        {
            return _seekTrace.ToArray();
        }
    }


    /// <summary>
    /// 清空跳转跟踪记录。
    /// </summary>
    public void ClearTrace()
    {
        lock (_seekTraceLock)
        {
            _seekTrace.Clear();
        }
    }

}

/// <summary>
/// 一次合并跳转及其所属的操作生命周期。
/// </summary>
/// <param name="Target">跳转目标。</param>
/// <param name="ResumePlaying">是否继续播放。</param>
/// <param name="Id">请求标识。</param>
/// <param name="FirstQueuedAt">首次入队时间。</param>
/// <param name="Count">本组合并次数。</param>
/// <param name="Token">入队时捕获的操作令牌。</param>
internal sealed record SeekRequest(TimeSpan Target, bool ResumePlaying, long Id, DateTimeOffset FirstQueuedAt, int Count, CancellationToken Token);
