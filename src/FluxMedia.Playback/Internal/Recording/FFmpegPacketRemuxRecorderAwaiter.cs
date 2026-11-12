namespace FluxMedia.FFmpeg.Pipeline;

/// <summary>
/// 等待录制任务完成后清理资源。
/// </summary>
internal static class FFmpegPacketRemuxRecorderAwaiter
{
    /// <summary>
    /// 等待后台任务结束后执行清理回调。
    /// </summary>
    public static async Task AwaitAndRunAsync(Task task, Action continuation)
    {
        await task.ConfigureAwait(false);
        continuation();
    }

    /// <summary>
    /// 后台任务失败时也执行资源清理。
    /// </summary>
    public static async Task AwaitAndFinallyAsync(Task task, Action cleanup)
    {
        try { await task.ConfigureAwait(false); }
        finally { cleanup(); }
    }
}
