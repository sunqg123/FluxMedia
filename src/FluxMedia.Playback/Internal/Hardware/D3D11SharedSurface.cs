using Silk.NET.Core.Native;
using Silk.NET.Direct3D11;
using Silk.NET.DXGI;
using static Silk.NET.Core.Native.SilkMarshal;
using D3DDevice = Silk.NET.Direct3D11.ID3D11Device;
using D3DTexture2D = Silk.NET.Direct3D11.ID3D11Texture2D;
using D3DVideoDevice = Silk.NET.Direct3D11.ID3D11VideoDevice;
using DxgiFormat = Silk.NET.DXGI.Format;

namespace FluxMedia.Playback.Internal.Hardware;

/// <summary>
/// 持有共享纹理、视图和互斥锁。
/// </summary>
internal sealed unsafe class D3D11SharedSurface : IDisposable
{
    /// <summary>
    /// 写锁等待上限，避免异常设备阻止播放停止。
    /// </summary>
    private const uint WriteLockTimeoutMilliseconds = 1000;

    /// <summary>
    /// 未取得互斥锁的超时状态。
    /// </summary>
    private const int WaitTimeout = 0x102;

    /// <summary>
    /// 共享表面已失效的状态。
    /// </summary>
    private const int WaitAbandoned = 0x80;

    /// <summary>
    /// 纹理。
    /// </summary>
    private ComPtr<D3DTexture2D> _texture;

    /// <summary>
    /// 互斥锁。
    /// </summary>
    private ComPtr<IDXGIKeyedMutex> _mutex;

    /// <summary>
    /// 输出视图。
    /// </summary>
    private ComPtr<ID3D11VideoProcessorOutputView> _outputView;

    /// <summary>
    /// 是否写入已锁定。
    /// </summary>
    private bool _writeLocked;

    /// <summary>
    /// 初始化 D3D11 共享表面所需的状态和资源。
    /// </summary>
    public D3D11SharedSurface(D3DDevice* device, ComPtr<D3DVideoDevice> videoDevice, ComPtr<ID3D11VideoProcessorEnumerator> enumerator, int width, int height)
    {
        try
        {
            var description = new Texture2DDesc
            {
                Format = DxgiFormat.FormatB8G8R8A8Unorm,
                Width = (uint)width,
                Height = (uint)height,
                ArraySize = 1,
                MipLevels = 1,
                SampleDesc = new SampleDesc(1, 0),
                Usage = Usage.Default,
                BindFlags = (uint)(BindFlag.RenderTarget | BindFlag.ShaderResource),
                CPUAccessFlags = 0,
                MiscFlags = (uint)ResourceMiscFlag.SharedKeyedmutex
            };

            ComPtr<D3DTexture2D> texture = default;
            ThrowHResult(device->CreateTexture2D(&description, (SubresourceData*)null, texture.GetAddressOf()));
            _texture = texture;
            _mutex = _texture.QueryInterface<IDXGIKeyedMutex>();

            using var resource = _texture.QueryInterface<IDXGIResource>();
            void* sharedHandle = null;
            ThrowHResult(resource.GetSharedHandle(ref sharedHandle));
            SharedHandle = (nint)sharedHandle;

            var outputDescription = new VideoProcessorOutputViewDesc
            {
                ViewDimension = VpovDimension.Texture2D,
                Texture2D = new Tex2DVpov(0)
            };
            ComPtr<ID3D11VideoProcessorOutputView> outputView = default;
            ThrowHResult(videoDevice.CreateVideoProcessorOutputView((ID3D11Resource*)_texture.Handle, enumerator, &outputDescription, outputView.GetAddressOf()));
            _outputView = outputView;
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <summary>
    /// 共享句柄。
    /// </summary>
    public nint SharedHandle
    {
        get;
    }

    /// <summary>
    /// 输出视图。
    /// </summary>
    public ComPtr<ID3D11VideoProcessorOutputView> OutputView => _outputView;

    /// <summary>
    /// 取得共享纹理的写入锁。
    /// </summary>
    public void BeginWrite()
    {
        EnsureMutexSuccess(_mutex.AcquireSync(0, WriteLockTimeoutMilliseconds));
        _writeLocked = true;
    }

    /// <summary>
    /// 完成纹理写入并交给显示端。
    /// </summary>
    public void EndWrite()
    {
        if (!_writeLocked)
        {
            return;
        }

        EnsureMutexSuccess(_mutex.ReleaseSync(1));
        _writeLocked = false;
    }

    /// <summary>
    /// 销毁失败纹理前，尽力归还已取得的写锁。
    /// </summary>
    public void CancelWrite()
    {
        if (!_writeLocked)
        {
            return;
        }

        _mutex.ReleaseSync(0);
        _writeLocked = false;
    }

    /// <summary>
    /// 仅将已确认释放的共享表面重置为写入状态。
    /// </summary>
    public bool TryResetForReuse()
    {
        var result = _mutex.AcquireSync(1, 0);
        if (result == 0)
            return _mutex.ReleaseSync(0) == 0;
        if (result != WaitTimeout)
            return false;
        // 显示端可能已交回键 0；确认所有权后才能继续复用。
        return _mutex.AcquireSync(0, 0) == 0 && _mutex.ReleaseSync(0) == 0;
    }

    /// <summary>
    /// 只接受 S_OK，超时和失效不能按普通成功处理。
    /// </summary>
    private static void EnsureMutexSuccess(int result)
    {
        if (result == 0)
            return;
        if (result == WaitTimeout)
            throw new TimeoutException("等待 D3D11 共享纹理互斥锁超时。");
        if (result == WaitAbandoned)
            throw new InvalidOperationException("D3D11 共享纹理互斥锁已失效，必须重建纹理。");
        ThrowHResult(result);
        throw new InvalidOperationException($"D3D11 互斥锁返回了意外状态：0x{result:X8}。");
    }

    /// <summary>
    /// 释放当前对象持有的资源。
    /// </summary>
    public void Dispose()
    {
        _outputView.Dispose();
        _mutex.Dispose();
        _texture.Dispose();
    }
}
