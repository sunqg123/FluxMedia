using FFmpeg.AutoGen;
using FluxMedia.Core.Configuration;
using FluxMedia.Core.Logging;
using FluxMedia.Core.Models;

namespace FluxMedia.Playback.Internal;

/// <summary>
/// 生成播放参数并计算网络超时。
/// </summary>
internal static unsafe class PlaybackFFmpegOptions
{
    /// <summary>
    /// 将播放配置转换为 FFmpeg 打开参数。
    /// </summary>
    public static AVDictionary* CreateOpenDictionary(MediaSource source, PlaybackOptions options, Guid instanceId)
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

        FluxMediaLog.Write(LogLevel.Information, nameof(PlaybackFFmpegOptions), instanceId,
            $"Event=NetworkFFmpegOptions Options={DescribeNetworkOptions(options, values)}");
        return dictionary;
    }

    /// <summary>
    /// 生成用于诊断的 FFmpeg 参数说明。
    /// </summary>
    public static string Describe(MediaSource source, PlaybackOptions options)
    {
        if (source.Kind != MediaSourceKind.NetworkStream)
        {
            return "local-file-defaults";
        }

        return DescribeNetworkOptions(options, CreateNetworkOptions(source, options));
    }

    /// <summary>
    /// 按传输协议生成唯一的网络参数集合。
    /// </summary>
    private static Dictionary<string, string> CreateNetworkOptions(MediaSource source, PlaybackOptions options)
    {
        var network = options.Network;
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["rw_timeout"] = ToMicroseconds(EffectiveReadTimeout(options)),
            ["timeout"] = ToMicroseconds(EffectiveOpenTimeout(options)),
            ["stimeout"] = ToMicroseconds(EffectiveOpenTimeout(options)),
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
            values["reconnect_delay_max"] = Math.Max(0, network.ReconnectDelayMax).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        return values;
    }

    /// <summary>
    /// 直接描述实际采用的参数，避免诊断规则漂移。
    /// </summary>
    private static string DescribeNetworkOptions(PlaybackOptions options, IReadOnlyDictionary<string, string> values)
    {
        var parameters = values.Select(option => $"{option.Key}={option.Value}");
        return $"mode={options.Network.Mode}, low_latency={options.Network.LowLatencyMode}, {string.Join(", ", parameters)}";
    }

    /// <summary>
    /// 创建可响应取消和超时的读取中断上下文。
    /// </summary>
    public static PlaybackInterruptContext CreateInterruptContext(TimeSpan timeout, CancellationToken cancellationToken)
    {
        return new PlaybackInterruptContext(timeout, cancellationToken);
    }

    /// <summary>
    /// 计算网络打开实际采用的超时时间。
    /// </summary>
    public static TimeSpan EffectiveOpenTimeout(PlaybackOptions options)
    {
        return MinPositive(options.Network.OpenTimeout, options.NetworkTimeout);
    }

    /// <summary>
    /// 计算网络读取实际采用的超时时间。
    /// </summary>
    public static TimeSpan EffectiveReadTimeout(PlaybackOptions options)
    {
        return MinPositive(options.Network.ReadTimeout, options.NetworkTimeout);
    }

    /// <summary>
    /// 取两个有效正时长中的较小值。
    /// </summary>
    private static TimeSpan MinPositive(TimeSpan first, TimeSpan second)
    {
        if (first <= TimeSpan.Zero)
        {
            return second;
        }

        if (second <= TimeSpan.Zero)
        {
            return first;
        }

        return first <= second ? first : second;
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
