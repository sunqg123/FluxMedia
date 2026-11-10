namespace FluxMedia.Core.Models;

/// <summary>
/// 通过共享句柄传递的 D3D11 纹理。
/// </summary>
/// <param name="SharedHandle">供显示端导入纹理的共享句柄。</param>
/// <param name="Width">视频表面的宽度，单位为像素。</param>
/// <param name="Height">视频表面的高度，单位为像素。</param>
/// <param name="PixelFormat">图像的像素格式。</param>
/// <param name="AcquireKey">显示端取得共享纹理锁时使用的键。</param>
/// <param name="ReleaseKey">显示端释放共享纹理锁时使用的键。</param>
public sealed record D3D11SharedTextureSurface(nint SharedHandle, int Width, int Height, PixelFormat PixelFormat, uint AcquireKey, uint ReleaseKey) : GpuVideoSurface(GpuVideoSurfaceKind.D3D11SharedTexture, Width, Height, PixelFormat);
