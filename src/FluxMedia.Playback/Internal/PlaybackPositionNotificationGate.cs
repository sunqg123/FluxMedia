namespace FluxMedia.Playback.Internal;

/// <summary>
/// 限制播放进度通知的发送频率。
/// </summary>
internal sealed class PlaybackPositionNotificationGate
{
    /// <summary>
    /// 间隔。
    /// </summary>
    private readonly TimeSpan _interval;

    /// <summary>
    /// 最近已发布时刻。
    /// </summary>
    private DateTimeOffset? _lastPublishedAt;

    /// <summary>
    /// 初始化播放位置通知同步锁所需的状态和资源。
    /// </summary>
    public PlaybackPositionNotificationGate(TimeSpan interval)
    {
        if (interval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(interval));
        }

        _interval = interval;
    }

    /// <summary>
    /// 判断是否到达下一次进度通知时间。
    /// </summary>
    public bool ShouldPublish(DateTimeOffset now)
    {
        if (_lastPublishedAt is not null && now - _lastPublishedAt.Value < _interval)
        {
            return false;
        }

        _lastPublishedAt = now;
        return true;
    }

    /// <summary>
    /// 清除通知时间，使下次进度立即发布。
    /// </summary>
    public void Reset() => _lastPublishedAt = null;
}
