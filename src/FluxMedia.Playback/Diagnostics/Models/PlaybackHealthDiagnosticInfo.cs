namespace FluxMedia.Playback;

/// <summary>
/// 播放健康状态及异常原因。
/// </summary>
public sealed record PlaybackHealthDiagnosticInfo
{
    /// <summary>
    /// 已确认失败时为 false，其他情况未作健康判定。
    /// </summary>
    public bool? IsHealthy { get; init; }

    /// <summary>
    /// 原因。
    /// </summary>
    public string? Reason { get; init; }

    /// <summary>
    /// 视频缓冲时长。
    /// </summary>
    public TimeSpan? VideoBufferedDuration { get; init; }

    /// <summary>
    /// 音频缓冲时长。
    /// </summary>
    public TimeSpan? AudioBufferedDuration { get; init; }

    /// <summary>
    /// 音频设备延迟。
    /// </summary>
    public TimeSpan? AudioDeviceDelay { get; init; }

    /// <summary>
    /// 检查时刻。
    /// </summary>
    public DateTimeOffset CheckedAt { get; init; }
}
