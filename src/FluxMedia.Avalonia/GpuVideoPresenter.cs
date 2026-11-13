using Avalonia.Platform;
using Avalonia.Rendering.Composition;
using FluxMedia.Core.Models;

namespace FluxMedia.Avalonia;

/// <summary>
/// 导入 GPU 图像，串行提交并管理导入资源。
/// </summary>
internal sealed class GpuVideoPresenter : IAsyncDisposable
{
    /// <summary>
    /// 显示端最多保留的导入图像数。
    /// </summary>
    private const int MaximumCachedImages = 8;

    /// <summary>
    /// 提交与释放共享同一边界，避免释放在途图像。
    /// </summary>
    private readonly SemaphoreSlim _submissionGate = new(1, 1);

    /// <summary>
    /// 合成器绘制表面。
    /// </summary>
    private readonly CompositionDrawingSurface _surface;

    /// <summary>
    /// 合成器 GPU 互操作接口。
    /// </summary>
    private readonly ICompositionGpuInterop _interop;

    /// <summary>
    /// 共享句柄对应的导入图像和最近使用顺序。
    /// </summary>
    private readonly Dictionary<nint, (ICompositionImportedGpuImage Image, long Use)> _images = new();

    /// <summary>
    /// 决定缓存淘汰顺序。
    /// </summary>
    private long _imageUse;

    /// <summary>
    /// 释放开始后拒绝新提交。
    /// </summary>
    private bool _disposed;

    /// <summary>
    /// 绑定合成器表面与导入接口。
    /// </summary>
    public GpuVideoPresenter(CompositionDrawingSurface surface, ICompositionGpuInterop interop)
    {
        _surface = surface;
        _interop = interop;
    }

    /// <summary>
    /// 当前绘制表面，清空后可重新显示。
    /// </summary>
    public CompositionDrawingSurface Surface => _surface;

    /// <summary>
    /// 是否支持 D3D11 共享纹理。
    /// </summary>
    public bool SupportsD3D11SharedTexture => _interop.SupportedImageHandleTypes.Contains(KnownPlatformGraphicsExternalImageHandleTypes.D3D11TextureGlobalSharedHandle);

    /// <summary>
    /// 是否支持 Vulkan 不透明文件描述符。
    /// </summary>
    public bool SupportsVulkanOpaqueFileDescriptor => _interop.SupportedImageHandleTypes.Contains(KnownPlatformGraphicsExternalImageHandleTypes.VulkanOpaquePosixFileDescriptor);

    /// <summary>
    /// 显示设备的适配器标识。
    /// </summary>
    public byte[]? AdapterLuid => _interop.DeviceLuid;

    /// <summary>
    /// 图像提交完成后才允许替换或释放导入资源。
    /// </summary>
    public async Task PresentAsync(GpuVideoSurface surface)
    {
        await _submissionGate.WaitAsync();
        try
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(GpuVideoPresenter));
            if (surface is not D3D11SharedTextureSurface texture)
                throw new NotSupportedException($"Unsupported GPU surface: {surface.Kind}");
            if (!SupportsD3D11SharedTexture)
                throw new NotSupportedException("The active Avalonia compositor cannot import D3D11 global shared handles.");

            if (_images.TryGetValue(texture.SharedHandle, out var entry) && entry.Image.IsLost)
            {
                await RemoveImageAsync(texture.SharedHandle);
                entry = default;
            }
            if (entry.Image is null)
            {
                if (_images.Count >= MaximumCachedImages)
                {
                    var oldest = _images.Aggregate((left, right) => left.Value.Use < right.Value.Use ? left : right);
                    await RemoveImageAsync(oldest.Key);
                }
                var image = _interop.ImportImage(new PlatformHandle(texture.SharedHandle,
                    KnownPlatformGraphicsExternalImageHandleTypes.D3D11TextureGlobalSharedHandle),
                    new PlatformGraphicsExternalImageProperties
                    {
                        Width = texture.Width,
                        Height = texture.Height,
                        Format = PlatformGraphicsExternalImageFormat.B8G8R8A8UNorm
                    });
                entry = (image, 0);
            }
            _images[texture.SharedHandle] = (entry.Image, ++_imageUse);
            await _surface.UpdateWithKeyedMutexAsync(entry.Image, texture.AcquireKey, texture.ReleaseKey);
        }
        finally
        {
            _submissionGate.Release();
        }
    }

    /// <summary>
    /// 从缓存移除后释放图像，失败时也不会重复使用。
    /// </summary>
    private async ValueTask RemoveImageAsync(nint handle)
    {
        if (!_images.TryGetValue(handle, out var entry))
            return;
        _images.Remove(handle);
        await entry.Image.DisposeAsync();
    }

    /// <summary>
    /// 等待在途提交结束，再尝试释放所有导入图像和表面。
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        await _submissionGate.WaitAsync();
        try
        {
            if (_disposed)
                return;
            _disposed = true;
            var errors = new List<Exception>();
            foreach (var image in _images.Values)
            {
                try
                {
                    await image.Image.DisposeAsync();
                }
                catch (Exception exception)
                {
                    errors.Add(exception);
                }
            }
            _images.Clear();
            try
            {
                _surface.Dispose();
            }
            catch (Exception exception)
            {
                errors.Add(exception);
            }
            if (errors.Count != 0)
                throw new AggregateException("GPU 显示资源释放失败。", errors);
        }
        finally
        {
            _submissionGate.Release();
        }
    }
}
