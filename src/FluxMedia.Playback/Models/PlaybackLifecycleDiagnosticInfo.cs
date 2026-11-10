namespace FluxMedia.Playback;

/// <summary>
/// 打开、播放和停止的关键时间点。
/// </summary>
public sealed record PlaybackLifecycleDiagnosticInfo
{
    /// <summary>
    /// 最近事件。
    /// </summary>
    public string? LastEvent { get; init; }

    /// <summary>
    /// 最近事件时刻。
    /// </summary>
    public DateTimeOffset? LastEventAt { get; init; }

    /// <summary>
    /// 已打开时刻。
    /// </summary>
    public DateTimeOffset? OpenedAt { get; init; }

    /// <summary>
    /// 播放已启动时刻。
    /// </summary>
    public DateTimeOffset? PlayStartedAt { get; init; }

    /// <summary>
    /// 界面首帧时刻。
    /// </summary>
    public DateTimeOffset? FirstUiFrameAt { get; init; }

    /// <summary>
    /// 已完成时刻。
    /// </summary>
    public DateTimeOffset? CompletedAt { get; init; }

    /// <summary>
    /// 停止已启动时刻。
    /// </summary>
    public DateTimeOffset? StopStartedAt { get; init; }

    /// <summary>
    /// 停止已完成时刻。
    /// </summary>
    public DateTimeOffset? StopCompletedAt { get; init; }

    /// <summary>
    /// 播放数量。
    /// </summary>
    public int PlayCount { get; init; }

    /// <summary>
    /// 暂停数量。
    /// </summary>
    public int PauseCount { get; init; }

    /// <summary>
    /// 停止数量。
    /// </summary>
    public int StopCount { get; init; }

    /// <summary>
    /// 自然完成信号数量。
    /// </summary>
    public int NaturalCompletionCount { get; init; }
}
