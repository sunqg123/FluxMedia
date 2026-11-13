namespace FluxMedia.Playback.Internal;

/// <summary>
/// 视频同步所需的音频时钟事实。
/// </summary>
internal sealed record AudioClockSnapshot
{
    /// <summary>
    /// 暂停、跳转和退出时共用的不可用时钟。
    /// </summary>
    public static AudioClockSnapshot Unavailable { get; } = new();

    /// <summary>
    /// 音频时钟当前是否可用于同步。
    /// </summary>
    public bool IsRunning
    {
        get; init;
    }

    /// <summary>
    /// 扣除设备缓冲后的媒体位置。
    /// </summary>
    public TimeSpan? PresentationTime
    {
        get; init;
    }

    /// <summary>
    /// 当前可听区间的实际倍速。
    /// </summary>
    public double PlaybackRate { get; init; } = 1;

    /// <summary>
    /// 设备中尚未播放的时长。
    /// </summary>
    public TimeSpan BufferedDuration
    {
        get; init;
    }

    /// <summary>
    /// 设备输出延迟。
    /// </summary>
    public TimeSpan DeviceDelay
    {
        get; init;
    }

    /// <summary>
    /// 时钟快照的更新时间。
    /// </summary>
    public DateTimeOffset UpdatedAt
    {
        get; init;
    }
}
