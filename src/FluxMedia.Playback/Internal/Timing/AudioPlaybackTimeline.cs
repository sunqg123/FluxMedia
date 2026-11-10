namespace FluxMedia.Playback.Internal;

/// <summary>
/// 将不同倍速的设备缓冲映射回媒体时间。
/// </summary>
internal sealed class AudioPlaybackTimeline
{
    /// <summary>
    /// 尚可能停留在设备中的倍速分段。
    /// </summary>
    private readonly LinkedList<(double WallStart, double MediaStart, double Rate)> _segments = new();
    /// <summary>
    /// 累计写入的设备时长。
    /// </summary>
    private double _writtenSeconds;
    /// <summary>
    /// 累计输出对应的媒体位置。
    /// </summary>
    private double _mediaSeconds;

    /// <summary>
    /// 最近写入的倍速。
    /// </summary>
    private double _lastRate = 1;

    /// <summary>
    /// 清除 Seek 前的时钟映射。
    /// </summary>
    public void Reset()
    {
        _segments.Clear();
        _writtenSeconds = 0;
        _lastRate = 1;
        _mediaSeconds = 0;
    }

    /// <summary>
    /// 按首个有效音频帧设置起点。
    /// </summary>
    public void Start(TimeSpan position, double rate)
    {
        if (_segments.Count != 0) return;
        _mediaSeconds = position.TotalSeconds;
        _lastRate = rate;
        _segments.AddLast((0, _mediaSeconds, rate));
    }

    /// <summary>
    /// 记录本次 PCM 的设备时长与倍速。
    /// </summary>
    public void Append(double seconds, double rate)
    {
        // 只在变速时增加分段，普通音频帧不分配节点。
        if (_segments.Count == 0 || _lastRate != rate)
            _segments.AddLast((_writtenSeconds, _mediaSeconds, rate));
        _lastRate = rate;
        _writtenSeconds += seconds;
        _mediaSeconds += seconds * rate;
    }

    /// <summary>
    /// 扣除设备缓冲，并返回当前可听位置和倍速。
    /// </summary>
    public TimeSpan? GetPosition(TimeSpan pending, out double rate)
    {
        rate = 1;
        if (_segments.Count == 0) return null;
        var played = Math.Max(0, _writtenSeconds - pending.TotalSeconds);
        while (_segments.First?.Next is { } next && next.Value.WallStart <= played)
            _segments.RemoveFirst();
        var segment = _segments.First!.Value;
        rate = segment.Rate;
        return TimeSpan.FromSeconds(Math.Max(0, segment.MediaStart + (played - segment.WallStart) * rate));
    }
}
