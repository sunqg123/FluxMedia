using Avalonia.Platform;
using Avalonia.Rendering.Composition;
using FluxMedia.Core.Models;

namespace FluxMedia.Avalonia;

/// <summary>
/// 将 GPU 视频帧提交给 Avalonia 合成器。
/// </summary>
internal sealed class GpuVideoPresenter : IAsyncDisposable
{
    /// <summary>
    /// 提交导入 GPU 图像的合成器绘制表面。
    /// </summary>
    private readonly CompositionDrawingSurface _surface;

    /// <summary>
    /// 合成器提供的 GPU 互操作接口。
    /// </summary>
    private readonly ICompositionGpuInterop _interop;

    /// <summary>
    /// 按共享句柄缓存的已导入 GPU 图像。
    /// </summary>
    private readonly Dictionary<nint, ICompositionImportedGpuImage> _d3d11Images = new();

    /// <summary>
    /// 是否已释放资源。
    /// </summary>
    private bool _disposed;

    /// <summary>
    /// 初始化 GPU 视频呈现器所需的状态和资源。
    /// </summary>
    public GpuVideoPresenter(CompositionDrawingSurface surface, ICompositionGpuInterop interop)
    {
        _surface = surface;
        _interop = interop;
    }

    /// <summary>
    /// 支持 D3D11 共享纹理。
    /// </summary>
    public bool SupportsD3D11SharedTexture => _interop.SupportedImageHandleTypes.Contains(KnownPlatformGraphicsExternalImageHandleTypes.D3D11TextureGlobalSharedHandle);

    /// <summary>
    /// 支持 Vulkan 不透明文件描述。
    /// </summary>
    public bool SupportsVulkanOpaqueFileDescriptor => _interop.SupportedImageHandleTypes.Contains(KnownPlatformGraphicsExternalImageHandleTypes.VulkanOpaquePosixFileDescriptor);

    /// <summary>
    /// 适配器 LUID。
    /// </summary>
    public byte[]? AdapterLuid => _interop.DeviceLuid;

    /// <summary>
    /// 设备 UUID。
    /// </summary>
    public byte[]? DeviceUuid => _interop.DeviceUuid;

    /// <summary>
    /// 将共享 GPU 表面提交给合成器。
    /// </summary>
    public async Task PresentAsync(GpuVideoSurface surface)
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(GpuVideoPresenter));
        }

        switch (surface)
        {
            case D3D11SharedTextureSurface d3d11:
                await PresentD3D11Async(d3d11).ConfigureAwait(true);
                break;
            case DmaBufVideoSurface:
                throw new NotSupportedException("The Vulkan DMA-BUF conversion bridge has not accepted this surface.");
            default:
                throw new NotSupportedException($"Unsupported GPU surface: {surface.Kind}");
        }
    }

    /// <summary>
    /// 释放当前对象持有的资源。
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var image in _d3d11Images.Values)
        {
            try
            {
                await image.DisposeAsync();
            }
            catch
            {
            }
        }
        _d3d11Images.Clear();
        _surface.Dispose();
    }

    /// <summary>
    /// 导入共享纹理并通过互斥锁提交显示。
    /// </summary>
    private Task PresentD3D11Async(D3D11SharedTextureSurface surface)
    {
        if (!SupportsD3D11SharedTexture)
        {
            throw new NotSupportedException("The active Avalonia compositor cannot import D3D11 global shared handles.");
        }

        if (!_d3d11Images.TryGetValue(surface.SharedHandle, out var image) || image.IsLost)
        {
            image = _interop.ImportImage(new PlatformHandle(surface.SharedHandle, KnownPlatformGraphicsExternalImageHandleTypes.D3D11TextureGlobalSharedHandle), new PlatformGraphicsExternalImageProperties
            {
                Width = surface.Width,
                Height = surface.Height,
                Format = PlatformGraphicsExternalImageFormat.B8G8R8A8UNorm
            });
            _d3d11Images[surface.SharedHandle] = image;
        }

        return _surface.UpdateWithKeyedMutexAsync(image, surface.AcquireKey, surface.ReleaseKey);
    }
}
