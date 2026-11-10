namespace FluxMedia.FFmpeg.Pipeline;

/// <summary>
/// 等待解复用任务退出后再清理资源。
/// </summary>
internal static class DemuxerTaskWaiter
{
    /// <summary>
    /// 等待后台任务退出。
    /// </summary>
    public static async Task WaitAsync(Task task, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var cancellation = new TaskCompletionSource<bool>();
        using var registration = cancellationToken.Register(static state => ((TaskCompletionSource<bool>)state!).TrySetCanceled(), cancellation);
        var completed = await Task.WhenAny(task, cancellation.Task).ConfigureAwait(false);
        await completed.ConfigureAwait(false);
        await task.ConfigureAwait(false);
    }
}
