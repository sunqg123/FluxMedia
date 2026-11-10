
namespace FluxMedia.Core.Models;

/// <summary>
/// GPU 视频帧的共享方式。
/// </summary>
public enum GpuVideoSurfaceKind
{
    /// <summary>
    /// D3D11 共享纹理。
    /// </summary>
    D3D11SharedTexture,
    /// <summary>
    /// DMA-BUF。
    /// </summary>
    DmaBuf
}


/// <summary>
/// 可供显示端导入的 GPU 视频表面。
/// </summary>
/// <param name="Kind">当前对象的媒体源或表面类型。</param>
/// <param name="Width">视频表面的宽度，单位为像素。</param>
/// <param name="Height">视频表面的高度，单位为像素。</param>
/// <param name="PixelFormat">图像的像素格式。</param>
public abstract record GpuVideoSurface(
    GpuVideoSurfaceKind Kind, int Width, int Height, PixelFormat PixelFormat);


/// <summary>
/// 通过共享句柄传递的 D3D11 纹理。
/// </summary>
/// <param name="SharedHandle">供显示端导入纹理的共享句柄。</param>
/// <param name="Width">视频表面的宽度，单位为像素。</param>
/// <param name="Height">视频表面的高度，单位为像素。</param>
/// <param name="PixelFormat">图像的像素格式。</param>
/// <param name="AcquireKey">显示端取得共享纹理锁时使用的键。</param>
/// <param name="ReleaseKey">显示端释放共享纹理锁时使用的键。</param>
public sealed record D3D11SharedTextureSurface(
    nint SharedHandle, int Width, int Height, PixelFormat PixelFormat, uint AcquireKey, uint ReleaseKey)
    : GpuVideoSurface(GpuVideoSurfaceKind.D3D11SharedTexture, Width, Height, PixelFormat);


/// <summary>
/// DMA-BUF 图像平面的偏移和行跨度。
/// </summary>
/// <param name="FileDescriptor">共享内存对象的文件描述符。</param>
/// <param name="ObjectIndex">平面引用的 DMA-BUF 对象索引。</param>
/// <param name="Offset">图像平面在共享对象中的字节偏移。</param>
/// <param name="Pitch">图像平面每行占用的字节数。</param>
public sealed record DmaBufPlane(
    int FileDescriptor, int ObjectIndex, long Offset, long Pitch);


/// <summary>
/// DMA-BUF 对象的文件描述符和布局信息。
/// </summary>
/// <param name="FileDescriptor">共享内存对象的文件描述符。</param>
/// <param name="Size">共享对象的总字节数。</param>
/// <param name="FormatModifier">DMA-BUF 的内存布局修饰符。</param>
public sealed record DmaBufObject(
    int FileDescriptor, ulong Size, ulong FormatModifier);


/// <summary>
/// 通过 DMA-BUF 共享的视频表面。
/// </summary>
/// <param name="Width">视频表面的宽度，单位为像素。</param>
/// <param name="Height">视频表面的高度，单位为像素。</param>
/// <param name="PixelFormat">图像的像素格式。</param>
/// <param name="DrmFormat">DMA-BUF 使用的 DRM 像素格式。</param>
/// <param name="Objects">共享图像使用的 DMA-BUF 对象列表。</param>
/// <param name="Planes">共享图像包含的平面布局。</param>
public sealed record DmaBufVideoSurface(
    int Width, int Height, PixelFormat PixelFormat, uint DrmFormat, IReadOnlyList<DmaBufObject> Objects, IReadOnlyList<DmaBufPlane> Planes)
    : GpuVideoSurface(GpuVideoSurfaceKind.DmaBuf, Width, Height, PixelFormat);
