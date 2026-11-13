using FFmpeg.AutoGen;

namespace FluxMedia.Playback.Internal.Hardware;

/// <summary>
/// 硬件解码像素格式查找。
/// </summary>
internal static unsafe class HardwarePixelFormats
{
    /// <summary>
    /// 使用硬件设备上下文的配置标记。
    /// </summary>
    private const int HardwareDeviceContextMethod = 0x01;

    /// <summary>
    /// 查找解码器支持的设备像素格式。
    /// </summary>
    public static AVPixelFormat Find(AVCodec* codec, AVHWDeviceType deviceType)
    {
        for (var index = 0; ; index++)
        {
            var configuration = ffmpeg.avcodec_get_hw_config(codec, index);
            if (configuration == null)
                return AVPixelFormat.AV_PIX_FMT_NONE;

            if (configuration->device_type == deviceType &&
                (configuration->methods & HardwareDeviceContextMethod) != 0)
            {
                return configuration->pix_fmt;
            }
        }
    }

    /// <summary>
    /// 从候选列表中选择指定格式。
    /// </summary>
    public static AVPixelFormat Select(AVPixelFormat* formats, AVPixelFormat preferred)
    {
        for (var current = formats; current != null && *current != AVPixelFormat.AV_PIX_FMT_NONE; current++)
        {
            if (*current == preferred)
                return *current;
        }

        return AVPixelFormat.AV_PIX_FMT_NONE;
    }
}
