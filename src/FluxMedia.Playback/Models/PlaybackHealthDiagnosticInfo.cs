namespace FluxMedia.Playback;

/// <summary>
/// 播放健康状态及异常原因。
/// </summary>
public sealed record PlaybackHealthDiagnosticInfo
{
    /// <summary>
    /// 是否健康。
    /// </summary>
    public bool IsHealthy { get; init; } = true;

    /// <summary>
    /// 是否已停滞。
    /// </summary>
    public bool IsStalled { get; init; }

    /// <summary>
    /// 原因。
    /// </summary>
    public string? Reason { get; init; }

    /// <summary>
    /// 最近界面帧距今时间。
    /// </summary>
    public TimeSpan? LastUiFrameAge { get; init; }

    /// <summary>
    /// 最近工作线程帧距今时间。
    /// </summary>
    public TimeSpan? LastWorkerFrameAge { get; init; }

    /// <summary>
    /// 视频缓冲时长。
    /// </summary>
    public TimeSpan? VideoBufferedDuration { get; init; }

    /// <summary>
    /// 是否工作线程运行中。
    /// </summary>
    public bool WorkerRunning { get; init; }

    /// <summary>
    /// 工作线程退出原因。
    /// </summary>
    public string? WorkerExitReason { get; init; }

    /// <summary>
    /// 音频工作线程状态。
    /// </summary>
    public string? AudioWorkerState { get; init; }

    /// <summary>
    /// 音频缓冲时长。
    /// </summary>
    public TimeSpan? AudioBufferedDuration { get; init; }

    /// <summary>
    /// 音频设备延迟。
    /// </summary>
    public TimeSpan? AudioDeviceDelay { get; init; }

    /// <summary>
    /// 音频视频偏差。
    /// </summary>
    public TimeSpan? AudioVideoDrift { get; init; }

    /// <summary>
    /// 检查时刻。
    /// </summary>
    public DateTimeOffset CheckedAt { get; init; }
}
