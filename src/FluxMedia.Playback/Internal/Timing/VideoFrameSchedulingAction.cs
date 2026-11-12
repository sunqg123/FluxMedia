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
    /// 等待时钟推进后重新判断，不计为重新缓冲。
    /// </summary>
    Wait,
    /// <summary>
    /// 缓冲。
    /// </summary>
    Buffer,
    /// <summary>
    /// 丢弃。
    /// </summary>
    Drop
}
