namespace FluxMedia.Playback;

/// <summary>
/// 播放会话状态。
/// </summary>
public enum PlaybackState
{
    /// <summary>
    /// 已创建。
    /// </summary>
    Created,
    /// <summary>
    /// 打开中。
    /// </summary>
    Opening,
    /// <summary>
    /// 就绪。
    /// </summary>
    Ready,
    /// <summary>
    /// 播放中。
    /// </summary>
    Playing,
    /// <summary>
    /// 已暂停。
    /// </summary>
    Paused,
    /// <summary>
    /// 跳转中。
    /// </summary>
    Seeking,
    /// <summary>
    /// 停止中。
    /// </summary>
    Stopping,
    /// <summary>
    /// 已停止。
    /// </summary>
    Stopped,
    /// <summary>
    /// 已完成。
    /// </summary>
    Completed,
    /// <summary>
    /// 失败。
    /// </summary>
    Failed,
    /// <summary>
    /// 已释放。
    /// </summary>
    Disposed
}
