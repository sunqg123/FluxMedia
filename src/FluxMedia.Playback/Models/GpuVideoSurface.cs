namespace FluxMedia.Core.Models;

/// <summary>
/// 可供显示端导入的 GPU 视频表面。
/// </summary>
/// <param name="Kind">当前对象的媒体源或表面类型。</param>
/// <param name="Width">视频表面的宽度，单位为像素。</param>
/// <param name="Height">视频表面的高度，单位为像素。</param>
/// <param name="PixelFormat">图像的像素格式。</param>
public abstract record GpuVideoSurface(GpuVideoSurfaceKind Kind, int Width, int Height, PixelFormat PixelFormat);
