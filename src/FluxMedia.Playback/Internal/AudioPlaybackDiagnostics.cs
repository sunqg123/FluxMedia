namespace FluxMedia.Playback.Internal;

/// <summary>
/// 音频解码进度和设备输出状态。
/// </summary>
internal sealed record AudioPlaybackDiagnostics
{
    /// <summary>
    /// 流索引。
    /// </summary>
    public int? StreamIndex { get; init; }

    /// <summary>
    /// 采样率。
    /// </summary>
    public int? SampleRate { get; init; }

    /// <summary>
    /// 声道。
    /// </summary>
    public int? Channels { get; init; }

    /// <summary>
    /// 输出格式。
    /// </summary>
    public string? OutputFormat { get; init; }

    /// <summary>
    /// 工作线程状态。
    /// </summary>
    public string WorkerState { get; init; } = "未启动";

    /// <summary>
    /// 已解码帧数量。
    /// </summary>
    public long DecodedFrameCount { get; init; }

    /// <summary>
    /// 已写入 PCM 字节。
    /// </summary>
    public long WrittenPcmBytes { get; init; }

    /// <summary>
    /// 当前处理到的媒体时间。
    /// </summary>
    public TimeSpan? CurrentTime { get; init; }

    /// <summary>
    /// 输出缓冲时长。
    /// </summary>
    public TimeSpan? OutputBufferedDuration { get; init; }

    /// <summary>
    /// 输出设备延迟。
    /// </summary>
    public TimeSpan? OutputDeviceDelay { get; init; }

    /// <summary>
    /// 最近错误。
    /// </summary>
    public string? LastError { get; init; }

    /// <summary>
    /// 输出设备状态。
    /// </summary>
    public string? OutputDeviceState { get; init; }

    /// <summary>
    /// 已更新时刻。
    /// </summary>
    public DateTimeOffset UpdatedAt { get; init; }
}
