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
    /// 实际位置。
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


/// <summary>
/// 一次跳转过程中的跟踪记录。
/// </summary>
/// <param name="Timestamp">当前帧或事件的时间戳。</param>
/// <param name="RequestId">当前跳转请求的唯一编号。</param>
/// <param name="Phase">本次跳转当前所处的执行阶段。</param>
/// <param name="Message">当前事件或操作的说明。</param>
/// <param name="Position">当前媒体位置。</param>
/// <param name="Elapsed">当前阶段已经过的时间。</param>
public sealed record PlaybackSeekTraceEntry(DateTimeOffset Timestamp, long RequestId, string Phase, string Message, TimeSpan? Position = null, TimeSpan? Elapsed = null);
