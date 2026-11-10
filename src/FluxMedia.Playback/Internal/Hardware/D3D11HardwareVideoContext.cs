using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen;
using FluxMedia.Core.Configuration;
using FluxMedia.Core.Logging;
using FluxMedia.Core.Models;
using FluxMedia.FFmpeg.Native;
using Silk.NET.Core;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D11;
using Silk.NET.DXGI;
using static Silk.NET.Core.Native.SilkMarshal;
using D3DDevice = Silk.NET.Direct3D11.ID3D11Device;
using D3DDeviceContext = Silk.NET.Direct3D11.ID3D11DeviceContext;
using D3DTexture2D = Silk.NET.Direct3D11.ID3D11Texture2D;
using D3DVideoContext = Silk.NET.Direct3D11.ID3D11VideoContext;
using D3DVideoDevice = Silk.NET.Direct3D11.ID3D11VideoDevice;
using DxgiFormat = Silk.NET.DXGI.Format;
using FFmpegD3DDevice = FFmpeg.AutoGen.ID3D11Device;

namespace FluxMedia.Playback.Internal.Hardware;

/// <summary>
/// 创建 D3D11 硬件解码和共享纹理上下文。
/// </summary>
internal sealed unsafe class D3D11HardwareVideoContext : IDisposable
{
    /// <summary>
    /// 硬件设备上下文方法。
    /// </summary>
    private const int HardwareDeviceContextMethod = 0x01;

    /// <summary>
    /// 实例标识。
    /// </summary>
    private readonly Guid _instanceId;

    /// <summary>
    /// 读取显示端输出能力的回调。
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
    private D3D11HardwareVideoContext(Guid instanceId, ComPtr<D3DDevice> device, ComPtr<D3DDeviceContext> deviceContext)
    {
        _instanceId = instanceId;
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
            var hardwarePixelFormat = FindHardwarePixelFormat(codec, AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA);
            if (hardwarePixelFormat == AVPixelFormat.AV_PIX_FMT_NONE)
            {
                error = "The selected FFmpeg decoder does not expose a D3D11VA hardware configuration.";
                return false;
            }

            CreateDevice(preferredAdapterLuid, out var device, out var deviceContext, out var adapterDescription);
            var created = new D3D11HardwareVideoContext(instanceId, device, deviceContext)
            {
                _hardwarePixelFormat = hardwarePixelFormat
            };
            context = created;
            created.InitializeFFmpegDevice();
            FluxMediaLog.Write(
                LogLevel.Information, nameof(D3D11HardwareVideoContext), instanceId, $"Event=HardwareVideoDeviceCreated Backend=D3D11VA Adapter={adapterDescription} PixelFormat={hardwarePixelFormat}");
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            context?.Dispose();
            context = null;
            FluxMediaLog.Write(
                LogLevel.Warning, nameof(D3D11HardwareVideoContext), instanceId, $"Event=HardwareVideoDeviceCreateFailed Backend=D3D11VA Error={ex.Message}", ex);
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
            _processor = new D3D11VideoProcessor(
                _device.Handle, _deviceContext.Handle, frame->width, frame->height, format.FramesPerSecond);
            _processorWidth = frame->width;
            _processorHeight = frame->height;
        }
        var owner = _processor.Convert(texture, arraySlice);
        var surface = new D3D11SharedTextureSurface(
            owner.SharedHandle, frame->width, frame->height, PixelFormat.Bgra32, AcquireKey: 1, ReleaseKey: 0);
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
            throw new InvalidOperationException(
                $"FFmpeg D3D11VA device initialization failed: {FFmpegErrorHelper.GetErrorMessage(result)}");
        }
    }

    /// <summary>
    /// 从候选格式中选择硬件像素格式。
    /// </summary>
    private AVPixelFormat SelectHardwarePixelFormat(AVCodecContext* _, AVPixelFormat* formats)
    {
        for (var current = formats; current != null && *current != AVPixelFormat.AV_PIX_FMT_NONE; current++)
        {
            if (*current == _hardwarePixelFormat)
            {
                return *current;
            }
        }

        return AVPixelFormat.AV_PIX_FMT_NONE;
    }

    /// <summary>
    /// 查找解码器支持的硬件像素格式。
    /// </summary>
    private static AVPixelFormat FindHardwarePixelFormat(AVCodec* codec, AVHWDeviceType deviceType)
    {
        for (var index = 0; ; index++)
        {
            var configuration = ffmpeg.avcodec_get_hw_config(codec, index);
            if (configuration == null)
            {
                return AVPixelFormat.AV_PIX_FMT_NONE;
            }

            if (configuration->device_type == deviceType &&
                (configuration->methods & HardwareDeviceContextMethod) != 0)
            {
                return configuration->pix_fmt;
            }
        }
    }

    /// <summary>
    /// 创建硬件解码所需的图形设备。
    /// </summary>
    private static void CreateDevice(
        byte[]? preferredAdapterLuid, out ComPtr<D3DDevice> device, out ComPtr<D3DDeviceContext> deviceContext, out string adapterDescription)
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
        ThrowHResult(d3d11.CreateDevice(
            adapter, D3DDriverType.Unknown, IntPtr.Zero, (uint)(CreateDeviceFlag.VideoSupport | CreateDeviceFlag.BgraSupport), featureLevels, 4, D3D11.SdkVersion, device.GetAddressOf(), &actualFeatureLevel, deviceContext.GetAddressOf()));

        AdapterDesc description;
        ThrowHResult(adapter.GetDesc(&description));
        adapterDescription = Marshal.PtrToStringUni((nint)description.Description) ?? "Unknown";
    }

    /// <summary>
    /// 选择与显示端匹配的图形适配器。
    /// </summary>
    private static ComPtr<IDXGIAdapter> SelectAdapter(
        ComPtr<IDXGIFactory1> factory, byte[]? preferredAdapterLuid)
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
    /// 表面数量。
    /// </summary>
    private int _surfaceCount;

    /// <summary>
    /// 是否已释放资源。
    /// </summary>
    private bool _disposed;

    /// <summary>
    /// 初始化 D3D11 视频处理器所需的状态和资源。
    /// </summary>
    public D3D11VideoProcessor(
        D3DDevice* device, D3DDeviceContext* deviceContext, int width, int height, double frameRate)
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
            ThrowHResult(_videoDevice.CreateVideoProcessorInputView(
                (ID3D11Resource*)inputTexture, _enumerator, &inputDescription, inputView.GetAddressOf()));
            using (inputView)
            {
                var stream = new VideoProcessorStream
                {
                    Enable = new Bool32(true),
                    PInputSurface = inputView
                };
                ThrowHResult(_videoContext.VideoProcessorBlt(
                    _processor, surface.OutputView, 0, 1, &stream));
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
            Interlocked.Decrement(ref _surfaceCount);
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

        Interlocked.Increment(ref _surfaceCount);
        return new D3D11SharedSurface(_device, _videoDevice, _enumerator, _width, _height);
    }
}

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
    public D3D11SharedSurface(
        D3DDevice* device, ComPtr<D3DVideoDevice> videoDevice, ComPtr<ID3D11VideoProcessorEnumerator> enumerator, int width, int height)
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
        ThrowHResult(device->CreateTexture2D(
            &description, (SubresourceData*)null, texture.GetAddressOf()));
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
        ThrowHResult(videoDevice.CreateVideoProcessorOutputView(
            (ID3D11Resource*)_texture.Handle, enumerator, &outputDescription, outputView.GetAddressOf()));
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

/// <summary>
/// 通过引用计数归还 D3D11 视频表面。
/// </summary>
internal sealed class D3D11GpuFrameOwner : IVideoFrameOwner
{
    /// <summary>
    /// 池。
    /// </summary>
    private D3D11VideoProcessor? _pool;

    /// <summary>
    /// 当前持有的 GPU 显示表面。
    /// </summary>
    private D3D11SharedSurface? _surface;

    /// <summary>
    /// 底层资源当前持有的引用数。
    /// </summary>
    private int _referenceCount = 1;

    /// <summary>
    /// 初始化 D3D11GPU 帧资源所有者所需的状态和资源。
    /// </summary>
    public D3D11GpuFrameOwner(D3D11VideoProcessor pool, D3D11SharedSurface surface)
    {
        _pool = pool;
        _surface = surface;
    }

    /// <summary>
    /// 共享句柄。
    /// </summary>
    public nint SharedHandle => _surface?.SharedHandle
        ?? throw new ObjectDisposedException(nameof(D3D11GpuFrameOwner));

    /// <summary>
    /// 增加底层资源的持有引用。
    /// </summary>
    public void AddReference()
    {
        while (true)
        {
            var current = Volatile.Read(ref _referenceCount);
            if (current <= 0)
            {
                throw new ObjectDisposedException(nameof(D3D11GpuFrameOwner));
            }

            if (Interlocked.CompareExchange(ref _referenceCount, current + 1, current) == current)
            {
                return;
            }
        }
    }

    /// <summary>
    /// 释放一个资源引用，最后一个引用负责清理。
    /// </summary>
    public void Release()
    {
        if (Interlocked.Decrement(ref _referenceCount) != 0)
        {
            return;
        }

        var pool = Interlocked.Exchange(ref _pool, null);
        var surface = Interlocked.Exchange(ref _surface, null);
        if (pool is not null && surface is not null)
        {
            pool.Return(surface);
        }
    }
}
