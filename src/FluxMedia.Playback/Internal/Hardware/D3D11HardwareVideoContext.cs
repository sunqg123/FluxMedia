using LogConverge.Sharp;
using FFmpeg.AutoGen;
using FluxMedia.Core.Models;
using FluxMedia.FFmpeg.Native;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D11;
using Silk.NET.DXGI;
using System.Runtime.InteropServices;
using static Silk.NET.Core.Native.SilkMarshal;
using D3DDevice = Silk.NET.Direct3D11.ID3D11Device;
using D3DDeviceContext = Silk.NET.Direct3D11.ID3D11DeviceContext;
using D3DTexture2D = Silk.NET.Direct3D11.ID3D11Texture2D;
using FFmpegD3DDevice = FFmpeg.AutoGen.ID3D11Device;

namespace FluxMedia.Playback.Internal.Hardware;

/// <summary>
/// 创建 D3D11 硬件解码和共享纹理上下文。
/// </summary>
internal sealed unsafe class D3D11HardwareVideoContext : IDisposable
{
    /// <summary>
    /// 供 FFmpeg 选择硬件像素格式的回调。
    /// </summary>
    private readonly AVCodecContext_get_format _getFormat;

    /// <summary>
    /// 设备。
    /// </summary>
    private ComPtr<D3DDevice> _device;

    /// <summary>
    /// 设备上下文。
    /// </summary>
    private ComPtr<D3DDeviceContext> _deviceContext;

    /// <summary>
    /// 硬件设备。
    /// </summary>
    private AVBufferRef* _hardwareDevice;

    /// <summary>
    /// 硬件像素格式。
    /// </summary>
    private AVPixelFormat _hardwarePixelFormat;

    /// <summary>
    /// 处理器。
    /// </summary>
    private D3D11VideoProcessor? _processor;

    /// <summary>
    /// 处理器宽度。
    /// </summary>
    private int _processorWidth;

    /// <summary>
    /// 处理器高度。
    /// </summary>
    private int _processorHeight;

    /// <summary>
    /// 是否已释放资源。
    /// </summary>
    private bool _disposed;

    /// <summary>
    /// 初始化 D3D11 硬件视频上下文所需的状态和资源。
    /// </summary>
    private D3D11HardwareVideoContext(ComPtr<D3DDevice> device, ComPtr<D3DDeviceContext> deviceContext)
    {
        _device = device;
        _deviceContext = deviceContext;
        _getFormat = SelectHardwarePixelFormat;
    }

    /// <summary>
    /// 尝试创建硬件解码上下文。
    /// </summary>
    public static bool TryCreate(AVCodec* codec, byte[]? preferredAdapterLuid, Guid instanceId, out D3D11HardwareVideoContext? context, out string? error)
    {
        context = null;
        error = null;
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            error = "D3D11VA is only available on Windows.";
            return false;
        }

        try
        {
            var hardwarePixelFormat = HardwarePixelFormats.Find(codec, AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA);
            if (hardwarePixelFormat == AVPixelFormat.AV_PIX_FMT_NONE)
            {
                error = "The selected FFmpeg decoder does not expose a D3D11VA hardware configuration.";
                return false;
            }

            CreateDevice(preferredAdapterLuid, out var device, out var deviceContext);
            var created = new D3D11HardwareVideoContext(device, deviceContext)
            {
                _hardwarePixelFormat = hardwarePixelFormat
            };
            context = created;
            created.InitializeFFmpegDevice();

            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            context?.Dispose();
            context = null;
            Log.Write(LogLevel.Warning, nameof(D3D11HardwareVideoContext),
                $"SessionId={instanceId} Event=HardwareVideoDeviceCreateFailed Backend=D3D11VA Error={ex.Message}", ex);
            return false;
        }
    }

    /// <summary>
    /// 将硬件设备上下文关联到解码器。
    /// </summary>
    public void ConfigureCodec(AVCodecContext* codecContext)
    {
        ThrowIfDisposed();
        codecContext->get_format = _getFormat;
        codecContext->hw_device_ctx = ffmpeg.av_buffer_ref(_hardwareDevice);
        if (codecContext->hw_device_ctx == null)
        {
            throw new InvalidOperationException("FFmpeg failed to retain the D3D11VA device context.");
        }
    }

    /// <summary>
    /// 将硬件帧转换为可共享的显示纹理。
    /// </summary>
    public VideoFrame ConvertFrame(
        AVFrame* frame, VideoFormat format, TimeSpan timestamp, long sequence)
    {
        ThrowIfDisposed();
        if ((AVPixelFormat)frame->format != _hardwarePixelFormat)
        {
            throw new InvalidOperationException($"Expected {_hardwarePixelFormat} hardware frame, received {(AVPixelFormat)frame->format}.");
        }

        var texture = (D3DTexture2D*)frame->data[0];
        if (texture == null)
        {
            throw new InvalidOperationException("D3D11VA frame did not contain a texture.");
        }

        var arraySlice = checked((uint)(nuint)frame->data[1]);
        if (_processor is null || _processorWidth != frame->width || _processorHeight != frame->height)
        {
            _processor?.Dispose();
            _processor = new D3D11VideoProcessor(_device.Handle, _deviceContext.Handle, frame->width, frame->height, format.FramesPerSecond);
            _processorWidth = frame->width;
            _processorHeight = frame->height;
        }
        var owner = _processor.Convert(texture, arraySlice);
        var surface = new D3D11SharedTextureSurface(owner.SharedHandle, frame->width, frame->height, PixelFormat.Bgra32, AcquireKey: 1, ReleaseKey: 0);
        return new VideoFrame(
            format with
            {
                PixelFormat = PixelFormat.Bgra32
            }, owner, surface, timestamp, sequence);
    }

    /// <summary>
    /// 释放当前对象持有的资源。
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _processor?.Dispose();
        _processor = null;
        if (_hardwareDevice != null)
        {
            var hardwareDevice = _hardwareDevice;
            _hardwareDevice = null;
            ffmpeg.av_buffer_unref(&hardwareDevice);
        }

        _deviceContext.Dispose();
        _device.Dispose();
    }

    /// <summary>
    /// 将图形设备注册到 FFmpeg 硬件上下文。
    /// </summary>
    private void InitializeFFmpegDevice()
    {
        _hardwareDevice = ffmpeg.av_hwdevice_ctx_alloc(AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA);
        if (_hardwareDevice == null)
        {
            throw new InvalidOperationException("FFmpeg could not allocate a D3D11VA device context.");
        }

        var deviceContext = (AVHWDeviceContext*)_hardwareDevice->data;
        var d3d11Context = (AVD3D11VADeviceContext*)deviceContext->hwctx;
        Marshal.AddRef((nint)_device.Handle);
        d3d11Context->device = (FFmpegD3DDevice*)_device.Handle;
        var result = ffmpeg.av_hwdevice_ctx_init(_hardwareDevice);
        if (result < 0)
        {
            throw new InvalidOperationException($"FFmpeg D3D11VA device initialization failed: {FFmpegErrorHelper.GetErrorMessage(result)}");
        }
    }

    /// <summary>
    /// 从候选格式中选择硬件像素格式。
    /// </summary>
    private AVPixelFormat SelectHardwarePixelFormat(AVCodecContext* _, AVPixelFormat* formats)
        => HardwarePixelFormats.Select(formats, _hardwarePixelFormat);

    /// <summary>
    /// 创建硬件解码所需的图形设备。
    /// </summary>
    private static void CreateDevice(byte[]? preferredAdapterLuid, out ComPtr<D3DDevice> device, out ComPtr<D3DDeviceContext> deviceContext)
    {
        using var dxgi = new DXGI(DXGI.CreateDefaultContext(["DXGI.dll"]));
        using var d3d11 = new D3D11(D3D11.CreateDefaultContext(["d3d11.dll"]));
        using var factory = dxgi.CreateDXGIFactory1<IDXGIFactory1>();
        using var adapter = SelectAdapter(factory, preferredAdapterLuid);

        var featureLevels = stackalloc D3DFeatureLevel[]
        {
            D3DFeatureLevel.Level121,
            D3DFeatureLevel.Level120,
            D3DFeatureLevel.Level111,
            D3DFeatureLevel.Level110
        };

        device = default;
        deviceContext = default;
        D3DFeatureLevel actualFeatureLevel;
        ThrowHResult(d3d11.CreateDevice(adapter, D3DDriverType.Unknown, IntPtr.Zero, (uint)(CreateDeviceFlag.VideoSupport | CreateDeviceFlag.BgraSupport), featureLevels, 4, D3D11.SdkVersion, device.GetAddressOf(), &actualFeatureLevel, deviceContext.GetAddressOf()));
    }

    /// <summary>
    /// 选择与显示端匹配的图形适配器。
    /// </summary>
    private static ComPtr<IDXGIAdapter> SelectAdapter(ComPtr<IDXGIFactory1> factory, byte[]? preferredAdapterLuid)
    {
        for (uint index = 0; ; index++)
        {
            ComPtr<IDXGIAdapter> adapter = default;
            var result = factory.EnumAdapters(index, adapter.GetAddressOf());
            if (result < 0)
            {
                break;
            }

            if (preferredAdapterLuid is null || preferredAdapterLuid.Length < 8)
            {
                return adapter;
            }

            AdapterDesc description;
            ThrowHResult(adapter.GetDesc(&description));
            var expectedLuid = BitConverter.ToInt64(preferredAdapterLuid, 0);
            var actualLuid = *(long*)&description.AdapterLuid;
            if (actualLuid == expectedLuid)
            {
                return adapter;
            }

            adapter.Dispose();
        }
        throw new InvalidOperationException("No D3D11 adapter matched the Avalonia compositor adapter LUID.");
    }

    /// <summary>
    /// 对象已释放时阻止继续操作。
    /// </summary>
    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(D3D11HardwareVideoContext));
        }
    }
}
