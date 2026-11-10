namespace FluxMedia.Playback;

/// <summary>
/// 当前播放输出录制状态。
/// </summary>
public enum PlaybackRecordingState
{
    /// <summary>
    /// 空闲。
    /// </summary>
    Idle,
    /// <summary>
    /// 启动中。
    /// </summary>
    Starting,
    /// <summary>
    /// 录制。
    /// </summary>
    Recording,
    /// <summary>
    /// 停止中。
    /// </summary>
    Stopping,
    /// <summary>
    /// 已完成。
    /// </summary>
    Completed,
    /// <summary>
    /// 失败。
    /// </summary>
    Failed
}
