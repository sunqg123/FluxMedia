using FluxMedia.Core.Errors;
using FluxMedia.Core.Logging;

namespace FluxMedia.Core.State;

/// <summary>
/// 多媒体会话通用异步状态机基类，统一处理状态变更、释放检查和异常通知。
/// </summary>
internal abstract class AsyncMediaStateMachine<TState> : IAsyncDisposable where TState : struct, Enum
{
    /// <summary>
    /// 保护状态读写的同步锁。
    /// </summary>
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>
    /// 保护单次资源释放的锁。
    /// </summary>
    private readonly object _disposeSync = new();

    /// <summary>
    /// 确保多次释放共享同一个完成任务。
    /// </summary>
    private Task? _disposeTask;

    /// <summary>
    /// 是否已释放。
    /// </summary>
    private volatile bool _isDisposed;

    /// <summary>
    /// 初始化异步媒体状态状态机所需的状态和资源。
    /// </summary>
    protected AsyncMediaStateMachine(TState initialState, string componentName)
    {
        State = initialState;
        ComponentName = componentName;
    }

    /// <summary>
    /// 当前组件的唯一实例标识。
    /// </summary>
    public Guid InstanceId { get; } = Guid.NewGuid();

    /// <summary>
    /// 组件名称。
    /// </summary>
    public string ComponentName { get; }

    /// <summary>
    /// 当前运行状态。
    /// </summary>
    public TState State { get; private set; }

    /// <summary>
    /// 是否已完成资源释放。
    /// </summary>
    public bool IsDisposed => _isDisposed;

    /// <summary>
    /// 组件运行状态变化时发出的通知。
    /// </summary>
    public event EventHandler<TState>? StateChanged;

    /// <summary>
    /// 发生运行错误时发出的通知。
    /// </summary>
    public event EventHandler<FluxMediaError>? Faulted;

    /// <summary>
    /// 切换状态并向订阅方发出状态变更事件。
    /// </summary>
    protected async ValueTask TransitionAsync(TState nextState, CancellationToken cancellationToken = default)
    {
        var changed = false;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            var previous = State;
            if (EqualityComparer<TState>.Default.Equals(previous, nextState))
            {
                return;
            }
            if (!IsTransitionAllowed(previous, nextState))
            {
                throw new InvalidOperationException($"Invalid {ComponentName} state transition: {previous} -> {nextState}.");
            }

            State = nextState;
            changed = true;
            FluxMediaLog.Write(Configuration.LogLevel.Debug, ComponentName, InstanceId, $"状态已变更：{previous} -> {nextState}");
        }
        finally
        {
            _gate.Release();
        }

        if (changed)
        {
            InvokeSubscribersSafely(StateChanged, nextState, nameof(StateChanged));
        }
    }

    /// <summary>
    /// 检查当前状态是否允许切换到目标状态。
    /// </summary>
    protected virtual bool IsTransitionAllowed(TState previousState, TState nextState) => true;

    /// <summary>
    /// 通知上层发生错误。错误会同时写入日志并触发 Faulted 事件。
    /// </summary>
    protected void NotifyFault(FluxMediaError error, Exception? exception = null)
    {
        FluxMediaLog.Write(Configuration.LogLevel.Error, ComponentName, InstanceId, error.Message, exception);
        InvokeSubscribersSafely(Faulted, error, nameof(Faulted));
    }

    /// <summary>
    /// 逐个通知订阅者，隔离单个回调异常。
    /// </summary>
    protected void InvokeSubscribersSafely<TEvent>(EventHandler<TEvent>? handlers, TEvent value, string eventName)
    {
        if (handlers is null)
        {
            return;
        }

        foreach (EventHandler<TEvent> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, value);
            }
            catch (Exception ex)
            {
                FluxMediaLog.Write(
                    Configuration.LogLevel.Error, ComponentName, InstanceId, $"Event=SubscriberFault SubscriberEvent={eventName} Error={ex.Message}", ex);
            }
        }
    }

    /// <summary>
    /// 如果对象已释放，则抛出统一的 FluxMedia 异常。
    /// </summary>
    protected void ThrowIfDisposed()
    {
        if (_isDisposed)
        {
            throw new FluxMediaException(new FluxMediaError(
                FluxMediaErrorCode.ResourceDisposed, $"{ComponentName} 已释放，不能继续操作。", ComponentName, InstanceId, State.ToString()));
        }
    }

    /// <summary>
    /// 释放当前对象持有的资源。
    /// </summary>
    public ValueTask DisposeAsync()
    {
        lock (_disposeSync)
        {
            _disposeTask ??= DisposeOnceAsync();
            return new ValueTask(_disposeTask);
        }
    }

    /// <summary>
    /// 确保资源释放逻辑只执行一次。
    /// </summary>
    private async Task DisposeOnceAsync()
    {
        try
        {
            await DisposeCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            _isDisposed = true;
        }
    }

    /// <summary>
    /// 清理当前组件的专属资源。
    /// </summary>
    protected virtual ValueTask DisposeCoreAsync() => default;
}
