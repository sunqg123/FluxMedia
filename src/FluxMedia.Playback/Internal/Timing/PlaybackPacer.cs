namespace FluxMedia.Playback.Internal;

/// <summary>
/// 按媒体时间戳计算当前帧需要等待的时间。
/// </summary>
internal static class PlaybackPacer
{
    /// <summary>
    /// 根据媒体时间和播放速度计算等待时长。
    /// </summary>
    public static TimeSpan CalculateDelay(TimeSpan timestamp, TimeSpan fallbackFrameInterval, PlaybackTimingMode timingMode, ref TimeSpan? firstMediaTimestamp, ref DateTimeOffset? presentationStartedAt, DateTimeOffset now, double playbackRate)
    {
        if (timingMode == PlaybackTimingMode.RealtimeLowLatency)
        {
            return TimeSpan.Zero;
        }

        firstMediaTimestamp ??= timestamp;
        presentationStartedAt ??= now;
        var rate = ClampPlaybackRate(playbackRate);
        var dueAt = presentationStartedAt.Value + TimeSpan.FromTicks((long)((timestamp - firstMediaTimestamp.Value).Ticks / rate));
        var delay = dueAt - now;
        if (delay <= TimeSpan.FromMilliseconds(2))
        {
            return TimeSpan.Zero;
        }

        return delay > TimeSpan.FromMilliseconds(250) ? fallbackFrameInterval : delay;
    }

    /// <summary>
    /// 将播放速度限制在支持范围内。
    /// </summary>
    public static double ClampPlaybackRate(double rate)
    {
        if (rate < 0.5)
        {
            return 0.5;
        }

        return rate > 2.0 ? 2.0 : rate;
    }
}
