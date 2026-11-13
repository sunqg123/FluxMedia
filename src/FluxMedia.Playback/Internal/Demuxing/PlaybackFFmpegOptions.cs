using FFmpeg.AutoGen;
using FluxMedia.Core.Models;

namespace FluxMedia.Playback.Internal;

/// <summary>
/// 生成实际传入 FFmpeg 的播放参数。
/// </summary>
internal static unsafe partial class PlaybackFFmpegOptions
{
    /// <summary>
    /// 将播放配置转换为 FFmpeg 打开参数。
    /// </summary>
    public static AVDictionary* CreateOpenDictionary(MediaSource source, PlaybackOptions options)
    {
        AVDictionary* dictionary = null;
        if (source.Kind != MediaSourceKind.NetworkStream)
        {
            return dictionary;
        }

        var values = CreateNetworkOptions(source, options);
        foreach (var option in values)
        {
            Set(&dictionary, option.Key, option.Value);
        }

        return dictionary;
    }

    /// <summary>
    /// 按传输协议生成唯一的网络参数集合。
    /// </summary>
    private static Dictionary<string, string> CreateNetworkOptions(MediaSource source, PlaybackOptions options)
    {
        var network = options.Network;
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["rw_timeout"] = ToMicroseconds(network.ReadTimeout),
            ["timeout"] = ToMicroseconds(network.OpenTimeout),
            ["stimeout"] = ToMicroseconds(network.OpenTimeout),
            ["probesize"] = Math.Max(4096, network.ProbeSize).ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["analyzeduration"] = ToMicroseconds(network.AnalyzeDuration)
        };

        var protocol = MediaSourcePolicy.GetProtocol(source);
        if (protocol == "rtsp")
        {
            values["rtsp_transport"] = network.RtspTransport == RtspTransportMode.Udp ? "udp" : "tcp";
        }
        else if (network.ReconnectEnabled && (protocol is "http" or "https"))
        {
            values["reconnect"] = "1";
            values["reconnect_streamed"] = "1";
            values["reconnect_on_network_error"] = "1";
            values["reconnect_delay_max"] = network.FFmpegReconnectDelayMax.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        return values;
    }

    /// <summary>
    /// 创建可响应取消和超时的读取中断上下文。
    /// </summary>
    public static PlaybackInterruptContext CreateInterruptContext(TimeSpan timeout, CancellationToken cancellationToken)
    {
        return new PlaybackInterruptContext(timeout, cancellationToken);
    }

    /// <summary>
    /// 设置 FFmpeg 参数字典中的键值。
    /// </summary>
    private static void Set(AVDictionary** dictionary, string key, string value)
    {
        ffmpeg.av_dict_set(dictionary, key, value, 0);
    }

    /// <summary>
    /// 将时间间隔换算为微秒。
    /// </summary>
    private static string ToMicroseconds(TimeSpan timeout)
    {
        return Math.Max(1, (long)(timeout.TotalMilliseconds * 1000)).ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
}
