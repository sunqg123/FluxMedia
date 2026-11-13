namespace FluxMedia.Playback.Internal;

/// <summary>
/// 跳转执行与自适应合并必需的状态。
/// </summary>
internal sealed record PlaybackSeekState
{
    /// <summary>
    /// 执行中的跳转标识。
    /// </summary>
    public long RequestId { get; init; }

    /// <summary>
    /// 调整合并间隔所需的请求数量。
    /// </summary>
    public int CoalescedRequestCount { get; init; }

    /// <summary>
    /// 决定是否允许再次重建的执行路径。
    /// </summary>
    public PlaybackSeekPath Path { get; init; }

    /// <summary>
    /// 自适应退避使用的定位耗时。
    /// </summary>
    public TimeSpan? DemuxerSeekElapsed { get; init; }

    /// <summary>
    /// 自适应响应策略使用的整体耗时。
    /// </summary>
    public TimeSpan? TotalElapsed { get; init; }

    /// <summary>
    /// 自适应恢复策略使用的首帧等待时间。
    /// </summary>
    public TimeSpan? FirstFrameElapsed { get; init; }

    /// <summary>
    /// 是否已经尝试过首帧恢复。
    /// </summary>
    public bool RecoveryAttempted { get; init; }
}
