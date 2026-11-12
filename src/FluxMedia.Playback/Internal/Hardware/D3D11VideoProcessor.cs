using Silk.NET.Core;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D11;
using Silk.NET.DXGI;
using System.Collections.Concurrent;
using static Silk.NET.Core.Native.SilkMarshal;
using D3DDevice = Silk.NET.Direct3D11.ID3D11Device;
using D3DDeviceContext = Silk.NET.Direct3D11.ID3D11DeviceContext;
using D3DTexture2D = Silk.NET.Direct3D11.ID3D11Texture2D;
using D3DVideoContext = Silk.NET.Direct3D11.ID3D11VideoContext;
using D3DVideoDevice = Silk.NET.Direct3D11.ID3D11VideoDevice;

namespace FluxMedia.Playback.Internal.Hardware;

/// <summary>
/// 将硬件解码帧转换为可共享的显示纹理。
/// </summary>
internal sealed unsafe class D3D11VideoProcessor : IDisposable
{
    /// <summary>
    /// 表面池上限。
    /// </summary>
    private const int SurfacePoolLimit = 4;

    /// <summary>
    /// 设备。
    /// </summary>
    private readonly D3DDevice* _device;

    /// <summary>
    /// 设备上下文。
    /// </summary>
    private readonly D3DDeviceContext* _deviceContext;

    /// <summary>
    /// 视频设备。
    /// </summary>
    private readonly ComPtr<D3DVideoDevice> _videoDevice;

    /// <summary>
    /// 视频上下文。
    /// </summary>
    private readonly ComPtr<D3DVideoContext> _videoContext;

    /// <summary>
    /// 枚举器。
    /// </summary>
    private readonly ComPtr<ID3D11VideoProcessorEnumerator> _enumerator;

    /// <summary>
    /// 处理器。
    /// </summary>
    private readonly ComPtr<ID3D11VideoProcessor> _processor;

    /// <summary>
    /// 可用。
    /// </summary>
    private readonly ConcurrentQueue<D3D11SharedSurface> _available = new();

    /// <summary>
    /// 宽度。
    /// </summary>
    private readonly int _width;

    /// <summary>
    /// 高度。
    /// </summary>
    private readonly int _height;

    /// <summary>
    /// 是否已释放资源。
    /// </summary>
    private bool _disposed;

    /// <summary>
    /// 初始化 D3D11 视频处理器所需的状态和资源。
    /// </summary>
    public D3D11VideoProcessor(D3DDevice* device, D3DDeviceContext* deviceContext, int width, int height, double frameRate)
    {
        _device = device;
        _deviceContext = deviceContext;
        _videoDevice = device->QueryInterface<D3DVideoDevice>();
        _videoContext = deviceContext->QueryInterface<D3DVideoContext>();
        _width = width;
        _height = height;

        var rate = Math.Max(1u, (uint)Math.Round(frameRate <= 0 ? 25 : frameRate));
        var content = new VideoProcessorContentDesc
        {
            InputFrameFormat = VideoFrameFormat.Progressive,
            InputFrameRate = new Rational(rate, 1),
            InputWidth = (uint)width,
            InputHeight = (uint)height,
            OutputFrameRate = new Rational(rate, 1),
            OutputWidth = (uint)width,
            OutputHeight = (uint)height,
            Usage = VideoUsage.PlaybackNormal
        };

        ComPtr<ID3D11VideoProcessorEnumerator> enumerator = default;
        ThrowHResult(_videoDevice.CreateVideoProcessorEnumerator(&content, enumerator.GetAddressOf()));
        _enumerator = enumerator;

        ComPtr<ID3D11VideoProcessor> processor = default;
        ThrowHResult(_videoDevice.CreateVideoProcessor(_enumerator, 0, processor.GetAddressOf()));
        _processor = processor;
        _videoContext.VideoProcessorSetStreamFrameFormat(_processor, 0, VideoFrameFormat.Progressive);
    }

    /// <summary>
    /// 将解码图像转换为显示格式。
    /// </summary>
    public D3D11GpuFrameOwner Convert(D3DTexture2D* inputTexture, uint arraySlice)
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(D3D11VideoProcessor));
        }

        var surface = RentSurface();
        surface.BeginWrite();
        try
        {
            var inputDescription = new VideoProcessorInputViewDesc
            {
                FourCC = 0,
                ViewDimension = VpivDimension.Texture2D,
                Texture2D = new Tex2DVpiv(0, arraySlice)
            };
            ComPtr<ID3D11VideoProcessorInputView> inputView = default;
            ThrowHResult(_videoDevice.CreateVideoProcessorInputView((ID3D11Resource*)inputTexture, _enumerator, &inputDescription, inputView.GetAddressOf()));
            using (inputView)
            {
                var stream = new VideoProcessorStream
                {
                    Enable = new Bool32(true),
                    PInputSurface = inputView
                };
                ThrowHResult(_videoContext.VideoProcessorBlt(_processor, surface.OutputView, 0, 1, &stream));
                _deviceContext->Flush();
            }

            surface.EndWrite();
            return new D3D11GpuFrameOwner(this, surface);
        }
        catch
        {
            surface.CancelWrite();
            Return(surface);
            throw;
        }
    }

    /// <summary>
    /// 释放当前对象持有的资源。
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        while (_available.TryDequeue(out var surface))
        {
            surface.Dispose();
        }

        _processor.Dispose();
        _enumerator.Dispose();
        _videoContext.Dispose();
        _videoDevice.Dispose();
    }

    /// <summary>
    /// 将不再使用的纹理归还表面池。
    /// </summary>
    internal void Return(D3D11SharedSurface surface)
    {
        surface.ResetForReuse();
        if (_disposed || _available.Count >= SurfacePoolLimit)
        {
            surface.Dispose();
            return;
        }

        _available.Enqueue(surface);
    }

    /// <summary>
    /// 从纹理池租用可写的视频表面。
    /// </summary>
    private D3D11SharedSurface RentSurface()
    {
        if (_available.TryDequeue(out var surface))
        {
            return surface;
        }

        return new D3D11SharedSurface(_device, _videoDevice, _enumerator, _width, _height);
    }
}
