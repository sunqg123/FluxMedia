namespace FluxMedia.Playback;

/// <summary>
/// 当前播放输出录制诊断。
/// </summary>
public sealed record PlaybackRecordingDiagnosticInfo
{
    /// <summary>
    /// 当前运行状态。
    /// </summary>
    public PlaybackRecordingState State { get; init; }

    /// <summary>
    /// 输出路径。
    /// </summary>
    public string? OutputPath { get; init; }

    /// <summary>
    /// 已成功写入的数据包数量。
    /// </summary>
    public long EncodedFrameCount { get; init; }

    /// <summary>
    /// 本次查询的文件大小；读取失败时为空。
    /// </summary>
    public long? OutputFileSize { get; init; }

    /// <summary>
    /// 本次查询的文件存在状态；读取失败时为空。
    /// </summary>
    public bool? OutputFileExists { get; init; }

    /// <summary>
    /// 文件尾和 IO 关闭均成功；不代表文件含有效媒体数据。
    /// </summary>
    public bool OutputFileFinalized { get; init; }

    /// <summary>
    /// 源视频编码名称，未知时为空。
    /// </summary>
    public string? VideoCodec { get; init; }

    /// <summary>
    /// 是否包含音频。
    /// </summary>
    public bool? IncludesAudio { get; init; }

    /// <summary>
    /// 最近错误；成功完成后清空。
    /// </summary>
    public string? LastError { get; init; }
}
