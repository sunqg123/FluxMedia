namespace FluxMedia.Playback;

/// <summary>
/// 记录跳转路径、解码同步及首帧恢复耗时。
/// </summary>
public sealed record PlaybackSeekDiagnosticInfo
{
    /// <summary>
    /// 请求标识。
    /// </summary>
    public long RequestId { get; init; }

    /// <summary>
    /// 请求位置。
    /// </summary>
    public TimeSpan? RequestedPosition { get; init; }

    /// <summary>
    /// 跳转后首个有效输出帧的位置；无输出帧时为空。
    /// </summary>
    public TimeSpan? ActualPosition { get; init; }

    /// <summary>
    /// 路径。
    /// </summary>
    public string? Path { get; init; }

    /// <summary>
    /// 策略。
    /// </summary>
    public string? Strategy { get; init; }

    /// <summary>
    /// 是否内进度。
    /// </summary>
    public bool InProgress { get; init; }

    /// <summary>
    /// 已合并请求数量。
    /// </summary>
    public int CoalescedRequestCount { get; init; }

    /// <summary>
    /// 下一次合并延迟。
    /// </summary>
    public TimeSpan? NextCoalesceDelay { get; init; }

    /// <summary>
    /// 合并耗时。
    /// </summary>
    public TimeSpan? CoalesceElapsed { get; init; }

    /// <summary>
    /// 解复用器跳转耗时。
    /// </summary>
    public TimeSpan? DemuxerSeekElapsed { get; init; }

    /// <summary>
    /// 总计耗时。
    /// </summary>
    public TimeSpan? TotalElapsed { get; init; }

    /// <summary>
    /// 界面首帧耗时。
    /// </summary>
    public TimeSpan? FirstUiFrameElapsed { get; init; }

    /// <summary>
    /// 是否首次界面恢复已尝试。
    /// </summary>
    public bool FirstUiRecoveryAttempted { get; init; }

    /// <summary>
    /// 恢复原因。
    /// </summary>
    public string? RecoveryReason { get; init; }

    /// <summary>
    /// 已启动时刻。
    /// </summary>
    public DateTimeOffset? StartedAt { get; init; }

    /// <summary>
    /// 已完成时刻。
    /// </summary>
    public DateTimeOffset? CompletedAt { get; init; }

    /// <summary>
    /// 重新同步版本。
    /// </summary>
    public long? ResyncVersion { get; init; }

    /// <summary>
    /// 最近错误。
    /// </summary>
    public string? LastError { get; init; }

    /// <summary>
    /// 跟踪。
    /// </summary>
    public IReadOnlyList<PlaybackSeekTraceEntry> Trace { get; init; } = Array.Empty<PlaybackSeekTraceEntry>();
}
