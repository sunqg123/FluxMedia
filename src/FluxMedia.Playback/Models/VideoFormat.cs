namespace FluxMedia.Core.Models;

/// <summary>
/// 视频格式描述。
/// </summary>
/// <param name="Width">视频表面的宽度，单位为像素。</param>
/// <param name="Height">视频表面的高度，单位为像素。</param>
/// <param name="FramesPerSecond">视频每秒显示的帧数。</param>
/// <param name="PixelFormat">图像的像素格式。</param>
public sealed record VideoFormat(int Width, int Height, double FramesPerSecond, PixelFormat PixelFormat);
