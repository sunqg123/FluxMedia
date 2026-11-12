namespace FluxMedia.Playback.Internal;

/// <summary>
/// 按媒体时间戳计算当前帧需要等待的时间。
/// </summary>
internal static class PlaybackPacer
{
    /// <summary>
    /// 根据媒体时间和播放速度计算等待时长。
    /// </summary>
    public static TimeSpan CalculateDelay(TimeSpan timestamp, PlaybackTimingMode timingMode, ref TimeSpan? firstMediaTimestamp, ref DateTimeOffset? presentationStartedAt, DateTimeOffset now, double playbackRate)
    {
        if (timingMode == PlaybackTimingMode.RealtimeLowLatency)
        {
            return TimeSpan.Zero;
        }
        firstMediaTimestamp ??= timestamp;
        presentationStartedAt ??= now;
        var rate = PlaybackRatePolicy.Normalize(playbackRate);
        // 用时长差计算，避免极慢倍速生成超出日期范围的到期时间。
        var seconds = (timestamp - firstMediaTimestamp.Value).TotalSeconds / rate
            - (now - presentationStartedAt.Value).TotalSeconds;
        if (seconds <= 0.002)
        {
            return TimeSpan.Zero;
        }
        // 慢放也必须等待完整时间，由调度器分段等待并重新读取倍速。
        return PlaybackClock.MediaTime(seconds);
    }
}
