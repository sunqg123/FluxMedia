namespace FluxMedia.FFmpeg.Pipeline;

// 保存解复用器的跳转请求。
internal sealed unsafe partial class FFmpegSharedDemuxer
{
    /// <summary>
    /// 等待解复用线程处理的跳转请求。
    /// </summary>
    private sealed class DemuxSeekRequest
    {
        /// <summary>
        /// 通知当前操作完成的信号。
        /// </summary>
        private readonly TaskCompletionSource<bool> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>
        /// 初始化解复用跳转请求所需的状态和资源。
        /// </summary>
        public DemuxSeekRequest(TimeSpan target)
        {
            Target = target;
        }

        /// <summary>
        /// 目标。
        /// </summary>
        public TimeSpan Target
        {
            get;
        }

        /// <summary>
        /// 是否已完成。
        /// </summary>
        public bool IsCompleted => _completion.Task.IsCompleted;

        /// <summary>
        /// 任务。
        /// </summary>
        public Task Task => _completion.Task;

        /// <summary>
        /// 尝试设置等待中的请求结果。
        /// </summary>
        public void TrySetResult() => _completion.TrySetResult(true);

        /// <summary>
        /// 尝试用异常结束等待中的请求。
        /// </summary>
        public void TrySetException(Exception exception) => _completion.TrySetException(exception);

        /// <summary>
        /// 尝试将等待中的请求标记为取消。
        /// </summary>
        public void TryCancel() => _completion.TrySetCanceled();
    }
}
