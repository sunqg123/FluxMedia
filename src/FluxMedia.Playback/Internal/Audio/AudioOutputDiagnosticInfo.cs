namespace FluxMedia.Playback.Internal.Audio;

/// <summary>
/// 音频输出诊断信息。
/// </summary>
internal sealed record AudioOutputDiagnosticInfo
{
    /// <summary>
    /// 后端名称。
    /// </summary>
    public string BackendName { get; init; } = "None";

    /// <summary>
    /// 当前运行状态。
    /// </summary>
    public AudioOutputState State { get; init; } = AudioOutputState.Created;

    /// <summary>
    /// 格式。
    /// </summary>
    public PcmAudioFormat? Format { get; init; }

    /// <summary>
    /// 已写入字节。
    /// </summary>
    public long WrittenBytes { get; init; }

    /// <summary>
    /// 最近错误。
    /// </summary>
    public string? LastError { get; init; }

    /// <summary>
    /// 是否静音。
    /// </summary>
    public bool IsMuted { get; init; }

    /// <summary>
    /// 音量。
    /// </summary>
    public double Volume { get; init; }

    /// <summary>
    /// 缓冲时长。
    /// </summary>
    public TimeSpan BufferedDuration { get; init; }

    /// <summary>
    /// 设备延迟。
    /// </summary>
    public TimeSpan DeviceDelay { get; init; }

}
