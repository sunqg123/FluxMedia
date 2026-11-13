namespace FluxMedia.Playback.Internal;

/// <summary>
/// 录制操作的实际结果。
/// </summary>
internal sealed record PlaybackRecordingResult
{
    /// <summary>
    /// 录制的实际状态。
    /// </summary>
    public PlaybackRecordingState State { get; init; }

    /// <summary>
    /// 录制输出路径。
    /// </summary>
    public string? OutputPath { get; init; }

    /// <summary>
    /// 封装与关闭是否均成功。
    /// </summary>
    public bool OutputFileFinalized { get; init; }

    /// <summary>
    /// 已成功写入的数据包数量。
    /// </summary>
    public long WrittenPackets { get; init; }

    /// <summary>
    /// 录制失败原因。
    /// </summary>
    public string? LastError { get; init; }
}
