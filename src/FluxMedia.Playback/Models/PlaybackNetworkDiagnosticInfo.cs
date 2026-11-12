namespace FluxMedia.Playback;

/// <summary>
/// 网络读取、超时和重连状态。
/// </summary>
public sealed record PlaybackNetworkDiagnosticInfo
{
    /// <summary>
    /// 是否网络源。
    /// </summary>
    public bool IsNetworkSource { get; init; }

    /// <summary>
    /// 是否重连已启用。
    /// </summary>
    public bool ReconnectEnabled { get; init; }

    /// <summary>
    /// 已配置重连尝试次数。
    /// </summary>
    public int ConfiguredReconnectAttempts { get; init; }

    /// <summary>
    /// 恢复尝试次数。
    /// </summary>
    public int RecoveryAttemptCount { get; init; }

    /// <summary>
    /// 读取超时数量。
    /// </summary>
    public int ReadTimeoutCount { get; init; }

    /// <summary>
    /// 最近数据包时刻。
    /// </summary>
    public DateTimeOffset? LastPacketAt { get; init; }

    /// <summary>
    /// 最近数据包距今时间。
    /// </summary>
    public TimeSpan? LastPacketAge { get; init; }

    /// <summary>
    /// 解复用器状态。
    /// </summary>
    public string? DemuxerState { get; init; }

    /// <summary>
    /// 解复用器退出原因。
    /// </summary>
    public string? DemuxerExitReason { get; init; }

    /// <summary>
    /// 最近网络错误。
    /// </summary>
    public string? LastNetworkError { get; init; }

    /// <summary>
    /// 当前或最近管线因缓冲策略丢弃的视频包数。
    /// </summary>
    public long DroppedVideoPacketCount { get; init; }

    /// <summary>
    /// 当前或最近管线因缓冲策略丢弃的音频包数。
    /// </summary>
    public long DroppedAudioPacketCount { get; init; }

    /// <summary>
    /// 当前或最近管线因 Seek 清理的视频包数。
    /// </summary>
    public long SeekDiscardedVideoPacketCount { get; init; }

    /// <summary>
    /// 当前或最近管线因 Seek 清理的音频包数。
    /// </summary>
    public long SeekDiscardedAudioPacketCount { get; init; }

    /// <summary>
    /// 当前或最近管线停止时清理的视频包数。
    /// </summary>
    public long StopDiscardedVideoPacketCount { get; init; }

    /// <summary>
    /// 当前或最近管线停止时清理的音频包数。
    /// </summary>
    public long StopDiscardedAudioPacketCount { get; init; }
}
