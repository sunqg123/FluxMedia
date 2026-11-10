using FluxMedia.Core.Logging;
using FluxMedia.Core.State;

namespace FluxMedia.Tests;

/// <summary>
/// 异步媒体状态状态机测试。
/// </summary>
public sealed class AsyncMediaStateMachineTests
{
    /// <summary>
    /// 验证并发释放只执行一次资源清理。
    /// </summary>
    [Fact]
    public async Task ConcurrentDispose_RunsDisposeCoreOnlyOnce()
    {
        var machine = new TestStateMachine();

        var first = machine.DisposeAsync().AsTask();
        await machine.DisposeStarted.Task;
        var second = machine.DisposeAsync().AsTask();
        machine.AllowDisposeToComplete.TrySetResult();

        await Task.WhenAll(first, second);

        Assert.Equal(1, machine.DisposeCallCount);
        Assert.True(machine.IsDisposed);
    }

    /// <summary>
    /// 验证订阅者异常不会使已完成的状态切换失败。
    /// </summary>
    [Fact]
    public async Task SubscriberException_DoesNotRejectCompletedTransition()
    {
        var machine = new TestStateMachine();
        machine.StateChanged += (_, _) => throw new InvalidOperationException("subscriber failure");

        await machine.MoveToRunningAsync();

        Assert.Equal(TestState.Running, machine.State);
        machine.AllowDisposeToComplete.TrySetResult();
        await machine.DisposeAsync();
    }

    /// <summary>
    /// 测试状态。
    /// </summary>
    private enum TestState
    {
        /// <summary>
        /// 已创建。
        /// </summary>
        Created,
        /// <summary>
        /// 运行中。
        /// </summary>
        Running,
        /// <summary>
        /// 已释放。
        /// </summary>
        Disposed
    }

    /// <summary>
    /// 测试状态状态机。
    /// </summary>
    private sealed class TestStateMachine : AsyncMediaStateMachine<TestState>
    {
        /// <summary>
        /// 释放调用数量。
        /// </summary>
        private int _disposeCallCount;

        /// <summary>
        /// 初始化测试状态状态机所需的状态和资源。
        /// </summary>
        public TestStateMachine()
            : base(TestState.Created, nameof(TestStateMachine))
        {
        }

        /// <summary>
        /// 释放调用数量。
        /// </summary>
        public int DisposeCallCount => Volatile.Read(ref _disposeCallCount);

        /// <summary>
        /// 释放已启动任务。
        /// </summary>
        public TaskCompletionSource DisposeStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>
        /// 允许释放转为结束任务。
        /// </summary>
        public TaskCompletionSource AllowDisposeToComplete { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>
        /// 将测试状态机切换到运行状态。
        /// </summary>
        public ValueTask MoveToRunningAsync() => TransitionAsync(TestState.Running);

        /// <summary>
        /// 清理当前组件的专属资源。
        /// </summary>
        protected override async ValueTask DisposeCoreAsync()
        {
            Interlocked.Increment(ref _disposeCallCount);
            DisposeStarted.TrySetResult();
            await AllowDisposeToComplete.Task.ConfigureAwait(false);
            await TransitionAsync(TestState.Disposed).ConfigureAwait(false);
        }
    }
}
