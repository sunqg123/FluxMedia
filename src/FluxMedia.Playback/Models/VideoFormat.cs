
namespace FluxMedia.Core.Models;

/// <summary>
/// 框架内部统一使用的视频像素格式。
/// </summary>
public enum PixelFormat
{
    /// <summary>
    /// 未知。
    /// </summary>
    Unknown,
    /// <summary>
    /// 32 位 RGBA 像素格式。
    /// </summary>
    Rgba32,
    /// <summary>
    /// 32 位 BGRA 像素格式。
    /// </summary>
    Bgra32,
    /// <summary>
    /// 24 位 RGB 像素格式。
    /// </summary>
    Rgb24,
    /// <summary>
    /// YUV420 平面像素格式。
    /// </summary>
    Yuv420P,
    /// <summary>
    /// NV12 像素格式。
    /// </summary>
    Nv12
}


/// <summary>
/// 视频格式描述。
/// </summary>
/// <param name="Width">视频表面的宽度，单位为像素。</param>
/// <param name="Height">视频表面的高度，单位为像素。</param>
/// <param name="FramesPerSecond">视频每秒显示的帧数。</param>
/// <param name="PixelFormat">图像的像素格式。</param>
public sealed record VideoFormat(int Width, int Height, double FramesPerSecond, PixelFormat PixelFormat);
