using FluxMedia.Core.Models;

namespace FluxMedia.Playback.Internal;

internal static partial class PlaybackFFmpegOptions
{
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
    /// 直接描述实际采用的参数，避免诊断规则漂移。
    /// </summary>
    private static string DescribeNetworkOptions(PlaybackOptions options, IReadOnlyDictionary<string, string> values)
    {
        var parameters = values.Select(option => $"{option.Key}={option.Value}");
        var localDelay = options.Network.ReconnectDelay.TotalMilliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return $"low_latency={options.Network.LowLatencyMode}, local_reconnect_delay_ms={localDelay}, {string.Join(", ", parameters)}";
    }
}
