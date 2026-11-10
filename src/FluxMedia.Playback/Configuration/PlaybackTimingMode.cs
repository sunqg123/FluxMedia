namespace FluxMedia.Playback;

/// <summary>
/// 视频帧的播放调度模式。
/// </summary>
public enum PlaybackTimingMode
{
    /// <summary>
    /// 根据媒体和平台自动选择。
    /// </summary>
    Auto,
    /// <summary>
    /// 呈现时间戳。
    /// </summary>
    PresentationTimestamp,
    /// <summary>
    /// 实时低延迟。
    /// </summary>
    RealtimeLowLatency
}
