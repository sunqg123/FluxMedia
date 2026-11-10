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
        var rate = PlaybackRatePolicy.Normalize(playbackRate);
        var dueAt = presentationStartedAt.Value + TimeSpan.FromTicks((long)((timestamp - firstMediaTimestamp.Value).Ticks / rate));
        var delay = dueAt - now;
        if (delay <= TimeSpan.FromMilliseconds(2))
        {
            return TimeSpan.Zero;
        }
        return delay > TimeSpan.FromMilliseconds(250) ? fallbackFrameInterval : delay;
    }
}
