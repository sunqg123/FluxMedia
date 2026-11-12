namespace FluxMedia.Playback;

/// <summary>
/// 录制开始后收到的播放数据包，不重新处理完整媒体。
/// </summary>
public sealed record PlaybackRecordingOptions
{
    /// <summary>
    /// 输出路径。
    /// </summary>
    public string OutputPath { get; init; } = string.Empty;

    /// <summary>
    /// 视频码率（千比特每秒）。
    /// </summary>
    public int VideoBitrateKbps { get; init; } = 4_000;

    /// <summary>
    /// 输出文件存在时是否允许覆盖。
    /// </summary>
    public bool OverwriteExisting { get; init; } = true;

    /// <summary>
    /// 输出目录不存在时是否自动创建。
    /// </summary>
    public bool CreateOutputDirectory { get; init; } = true;

}
