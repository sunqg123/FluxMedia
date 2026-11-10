namespace FluxMedia.Core.Models;

/// <summary>
/// 通过 DMA-BUF 共享的视频表面。
/// </summary>
/// <param name="Width">视频表面的宽度，单位为像素。</param>
/// <param name="Height">视频表面的高度，单位为像素。</param>
/// <param name="PixelFormat">图像的像素格式。</param>
/// <param name="DrmFormat">DMA-BUF 使用的 DRM 像素格式。</param>
/// <param name="Objects">共享图像使用的 DMA-BUF 对象列表。</param>
/// <param name="Planes">共享图像包含的平面布局。</param>
public sealed record DmaBufVideoSurface(int Width, int Height, PixelFormat PixelFormat, uint DrmFormat, IReadOnlyList<DmaBufObject> Objects, IReadOnlyList<DmaBufPlane> Planes) : GpuVideoSurface(GpuVideoSurfaceKind.DmaBuf, Width, Height, PixelFormat);
