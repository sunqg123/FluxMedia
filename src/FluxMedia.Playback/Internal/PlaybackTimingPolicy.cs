using FluxMedia.Core.Models;

namespace FluxMedia.Playback.Internal;

/// <summary>
/// 分开判断媒体内容属性与播放输出时序。
/// </summary>
internal static class PlaybackTimingPolicy
{
    /// <summary>
    /// 无有限总时长的网络源按直播处理，禁止时间跳转。
    /// </summary>
    public static bool IsLiveNetworkSource(MediaSource source, TimeSpan duration) => source.Kind == MediaSourceKind.NetworkStream && duration <= TimeSpan.Zero;

    /// <summary>
    /// 只有已确认有限总时长的点播允许 Seek。
    /// </summary>
    public static bool CanSeek(MediaSource source, TimeSpan duration) => !IsLiveNetworkSource(source, duration) && duration > TimeSpan.Zero;

    /// <summary>
    /// 时序选项只控制输出节奏，不改变直播与点播权限。
    /// </summary>
    public static PlaybackTimingMode Resolve(MediaSource source, PlaybackOptions options, TimeSpan duration)
    {
        if (options.Network.TimingMode != PlaybackTimingMode.Auto) return options.Network.TimingMode;
        return source.Kind == MediaSourceKind.File || duration > TimeSpan.Zero ? PlaybackTimingMode.PresentationTimestamp : PlaybackTimingMode.RealtimeLowLatency;
    }
}
