using System.Runtime.InteropServices;
using FFmpeg.AutoGen;
using FluxMedia.Core.Configuration;
using FluxMedia.Core.Logging;
using FluxMedia.FFmpeg.Native;

namespace FluxMedia.Playback.Internal.Hardware;

/// <summary>
/// 创建 VAAPI 硬件解码上下文。
/// </summary>
internal sealed unsafe class VaapiHardwareVideoContext : IDisposable
{
    /// <summary>
    /// 硬件设备上下文方法。
    /// </summary>
    private const int HardwareDeviceContextMethod = 0x01;

    /// <summary>
    /// 读取显示端输出能力的回调。
    /// </summary>
    private readonly AVCodecContext_get_format _getFormat;

    /// <summary>
    /// 硬件设备。
    /// </summary>
    private AVBufferRef* _hardwareDevice;

    /// <summary>
    /// 硬件像素格式。
    /// </summary>
    private AVPixelFormat _hardwarePixelFormat;

    /// <summary>
    /// 是否已释放资源。
    /// </summary>
    private bool _disposed;

    /// <summary>
    /// 初始化 VAAPI 硬件视频上下文所需的状态和资源。
    /// </summary>
    private VaapiHardwareVideoContext()
    {
        _getFormat = SelectHardwarePixelFormat;
    }

    /// <summary>
    /// 尝试创建硬件解码上下文。
    /// </summary>
    public static bool TryCreate(AVCodec* codec, Guid instanceId, out VaapiHardwareVideoContext? context, out string? error)
    {
        context = null;
        error = null;
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            error = "VAAPI is only available on Linux.";
            return false;
        }

        try
        {
            var hardwarePixelFormat = FindHardwarePixelFormat(codec, AVHWDeviceType.AV_HWDEVICE_TYPE_VAAPI);
            if (hardwarePixelFormat == AVPixelFormat.AV_PIX_FMT_NONE)
            {
                error = "The selected FFmpeg decoder does not expose a VAAPI hardware configuration.";
                return false;
            }

            var created = new VaapiHardwareVideoContext
            {
                _hardwarePixelFormat = hardwarePixelFormat
            };
            context = created;
            AVBufferRef* hardwareDevice = null;
            var result = ffmpeg.av_hwdevice_ctx_create(
                &hardwareDevice, AVHWDeviceType.AV_HWDEVICE_TYPE_VAAPI, null, null, 0);
            created._hardwareDevice = hardwareDevice;
            if (result < 0)
            {
                throw new InvalidOperationException($"FFmpeg VAAPI device creation failed: {FFmpegErrorHelper.GetErrorMessage(result)}");
            }
            FluxMediaLog.Write(LogLevel.Information, nameof(VaapiHardwareVideoContext), instanceId, $"Event=HardwareVideoDeviceCreated Backend=VAAPI PixelFormat={hardwarePixelFormat}");
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            context?.Dispose();
            context = null;
            FluxMediaLog.Write(
                LogLevel.Warning, nameof(VaapiHardwareVideoContext), instanceId, $"Event=HardwareVideoDeviceCreateFailed Backend=VAAPI Error={ex.Message}", ex);
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
            throw new InvalidOperationException("FFmpeg failed to retain the VAAPI device context.");
        }
    }

    /// <summary>
    /// 判断解码帧是否由硬件设备承载。
    /// </summary>
    public bool IsHardwareFrame(AVFrame* frame) => !_disposed && (AVPixelFormat)frame->format == _hardwarePixelFormat;

    /// <summary>
    /// 释放当前对象持有的资源。
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (_hardwareDevice != null)
        {
            var hardwareDevice = _hardwareDevice;
            _hardwareDevice = null;
            ffmpeg.av_buffer_unref(&hardwareDevice);
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

            if (configuration->device_type == deviceType && (configuration->methods & HardwareDeviceContextMethod) != 0)
            {
                return configuration->pix_fmt;
            }
        }
    }

    /// <summary>
    /// 对象已释放时阻止继续操作。
    /// </summary>
    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(VaapiHardwareVideoContext));
        }
    }
}
