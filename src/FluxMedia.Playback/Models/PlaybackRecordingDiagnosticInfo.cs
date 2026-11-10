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
    /// 已启动时刻。
    /// </summary>
    public DateTimeOffset? StartedAt { get; init; }

    /// <summary>
    /// 已停止时刻。
    /// </summary>
    public DateTimeOffset? StoppedAt { get; init; }

    /// <summary>
    /// 已录制时长。
    /// </summary>
    public TimeSpan RecordedDuration { get; init; }

    /// <summary>
    /// 已接收帧数量。
    /// </summary>
    public long ReceivedFrameCount { get; init; }

    /// <summary>
    /// 已编码帧数量。
    /// </summary>
    public long EncodedFrameCount { get; init; }

    /// <summary>
    /// 已丢弃帧数量。
    /// </summary>
    public long DroppedFrameCount { get; init; }

    /// <summary>
    /// 输出文件大小。
    /// </summary>
    public long OutputFileSize { get; init; }

    /// <summary>
    /// 是否输出文件存在。
    /// </summary>
    public bool OutputFileExists { get; init; }

    /// <summary>
    /// 输出文件是否已完成封装。
    /// </summary>
    public bool OutputFileFinalized { get; init; }

    /// <summary>
    /// 视频编解码器。
    /// </summary>
    public string VideoCodec { get; init; } = "mpeg4";

    /// <summary>
    /// 是否包含音频。
    /// </summary>
    public bool IncludesAudio { get; init; }

    /// <summary>
    /// 最近错误。
    /// </summary>
    public string? LastError { get; init; }
}
