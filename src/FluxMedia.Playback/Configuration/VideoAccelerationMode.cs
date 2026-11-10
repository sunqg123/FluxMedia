namespace FluxMedia.Playback;

/// <summary>
/// 视频硬件加速模式。
/// </summary>
public enum VideoAccelerationMode
{
    /// <summary>
    /// 已禁用。
    /// </summary>
    Disabled,
    /// <summary>
    /// 根据媒体和平台自动选择。
    /// </summary>
    Auto,
    /// <summary>
    /// 要求硬件。
    /// </summary>
    RequireHardware
}
