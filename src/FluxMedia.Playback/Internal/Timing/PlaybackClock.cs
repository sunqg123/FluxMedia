using System.Diagnostics;

namespace FluxMedia.Playback.Internal;

/// <summary>
/// 统计播放耗时并换算媒体时间。
/// </summary>
internal static class PlaybackClock
{
    /// <summary>
    /// 转换媒体时间，防止极端倍速使时间数值溢出。
    /// </summary>
    public static TimeSpan MediaTime(double seconds)
    {
        if (seconds <= 0)
            return TimeSpan.Zero;
        if (seconds >= TimeSpan.MaxValue.TotalSeconds)
            return TimeSpan.MaxValue;
        return TimeSpan.FromSeconds(seconds);
    }

    /// <summary>
    /// 根据计时器刻度计算已用时间。
    /// </summary>
    public static TimeSpan ElapsedSince(long startedAt)
    {
        return TimeSpan.FromSeconds((Stopwatch.GetTimestamp() - startedAt) / (double)Stopwatch.Frequency);
    }
}
