using System.Diagnostics;

namespace FluxMedia.Playback.Internal;

/// <summary>
/// 使用单调计时器计算播放阶段耗时。
/// </summary>
internal static class PlaybackClock
{
    /// <summary>
    /// 根据计时器刻度计算已用时间。
    /// </summary>
    public static TimeSpan ElapsedSince(long startedAt)
    {
        return TimeSpan.FromSeconds((Stopwatch.GetTimestamp() - startedAt) / (double)Stopwatch.Frequency);
    }

}
