namespace FluxMedia.Playback.Internal;

/// <summary>
/// 原子发布的换速状态，实际倍速只有这一份真值。
/// </summary>
internal sealed record PlaybackRateSnapshot
{
    /// <summary>
    /// 当前配置允许的最低倍速。
    /// </summary>
    public double MinRate
    {
        get; init;
    }

    /// <summary>
    /// 当前配置允许的最高倍速。
    /// </summary>
    public double MaxRate
    {
        get; init;
    }

    /// <summary>
    /// 最近登记的请求编号。
    /// </summary>
    public long RequestId
    {
        get; init;
    }

    /// <summary>
    /// 最近请求的目标倍速。
    /// </summary>
    public double RequestedRate { get; init; } = 1;

    /// <summary>
    /// 已提交的处理链倍速。
    /// </summary>
    public double AppliedRate { get; init; } = 1;

    /// <summary>
    /// 是否仍有请求等待应用。
    /// </summary>
    public bool Pending
    {
        get; init;
    }

    /// <summary>
    /// 最近换速请求的结果，不代表播放器健康状态。
    /// </summary>
    public string State { get; init; } = "Applied";

    /// <summary>
    /// 最近换速失败的原因。
    /// </summary>
    public string? LastError
    {
        get; init;
    }
}
