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

    /// <summary>
    /// 共享句柄。
    /// </summary>
    public nint SharedHandle { get; }

    /// <summary>
    /// 输出视图。
    /// </summary>
    public ComPtr<ID3D11VideoProcessorOutputView> OutputView => _outputView;

    /// <summary>
    /// 取得共享纹理的写入锁。
    /// </summary>
    public void BeginWrite()
    {
        ThrowHResult(_mutex.AcquireSync(0, uint.MaxValue));
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

        ThrowHResult(_mutex.ReleaseSync(1));
        _writeLocked = false;
    }

    /// <summary>
    /// 取消当前纹理写入并释放锁。
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
    /// 重置共享表面以供再次租用。
    /// </summary>
    public void ResetForReuse()
    {
        var result = _mutex.AcquireSync(1, 0);
        if (result >= 0)
        {
            _mutex.ReleaseSync(0);
        }
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
