namespace FluxMedia.FFmpeg.Pipeline;

/// <summary>
/// 等待录制任务完成后清理资源。
/// </summary>
internal static class FFmpegPacketRemuxRecorderAwaiter
{
    /// <summary>
    /// 等待后台任务结束后执行清理回调。
    /// </summary>
    public static async Task AwaitAndRunAsync(Task task, CancellationToken cancellationToken, Action continuation)
    {
        await task.ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        continuation();
    }
}
