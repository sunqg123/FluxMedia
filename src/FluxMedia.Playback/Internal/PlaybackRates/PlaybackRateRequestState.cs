namespace FluxMedia.Playback.Internal;

/// <summary>
/// 倍速请求的内部生命周期。
/// </summary>
internal enum PlaybackRateRequestState
{
    /// <summary>
    /// 等待准备或提交。
    /// </summary>
    Pending,
    /// <summary>
    /// 已进入不可逆提交。
    /// </summary>
    Committing,
    /// <summary>
    /// 处理链已采用新倍速。
    /// </summary>
    Applied,
    /// <summary>
    /// 请求失败或被作废。
    /// </summary>
    Rejected
}
