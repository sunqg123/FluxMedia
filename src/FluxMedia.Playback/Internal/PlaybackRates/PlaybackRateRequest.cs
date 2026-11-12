namespace FluxMedia.Playback.Internal;

/// <summary>
/// 一次换速的目标、计划及调用确认。
/// </summary>
internal sealed class PlaybackRateRequest
{
    /// <summary>
    /// 创建独立请求，续体不进入解码线程。
    /// </summary>
    public PlaybackRateRequest(long id, double rate, double[] stages)
    {
        Id = id;
        Rate = rate;
        Stages = stages;
    }

    /// <summary>
    /// 请求编号。
    /// </summary>
    public long Id { get; }

    /// <summary>
    /// 目标倍速。
    /// </summary>
    public double Rate { get; }

    /// <summary>
    /// 已检查的滤镜计划。
    /// </summary>
    public double[] Stages { get; }

    /// <summary>
    /// 由协调器短锁保护的状态。
    /// </summary>
    public PlaybackRateRequestState State { get; set; }

    /// <summary>
    /// 已返回接受，等待恢复或启动，不再使用调用超时。
    /// </summary>
    public bool Deferred { get; set; }

    /// <summary>
    /// 调用结果，可以先返回 Accepted。
    /// </summary>
    public TaskCompletionSource<PlaybackResult> Acknowledgement { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// 内部最终结果，供暂停和 Seek 等待提交结束。
    /// </summary>
    public TaskCompletionSource<PlaybackResult> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}
