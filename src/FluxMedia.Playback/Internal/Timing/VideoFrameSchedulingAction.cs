namespace FluxMedia.Playback.Internal;

/// <summary>
/// 当前视频帧应等待、显示还是丢弃。
/// </summary>
internal enum VideoFrameSchedulingAction
{
    /// <summary>
    /// 显示。
    /// </summary>
    Present,
    /// <summary>
    /// 缓冲。
    /// </summary>
    Buffer,
    /// <summary>
    /// 丢弃。
    /// </summary>
    Drop
}
