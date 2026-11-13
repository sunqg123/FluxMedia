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
    /// 每次断流配置的最大恢复尝试次数。
    /// </summary>
    public int ConfiguredReconnectAttempts { get; init; }

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


}
