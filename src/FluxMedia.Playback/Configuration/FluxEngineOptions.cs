namespace FluxMedia.Playback;

/// <summary>
/// 引擎启动所需的库路径与日志开关。
/// </summary>
public sealed record FluxEngineOptions
{
    /// <summary>
    /// FFmpeg 共享库目录。
    /// </summary>
    public string? FFmpegPath { get; init; }

    /// <summary>
    /// 是否开启默认文件日志。
    /// </summary>
    public bool EnableLogging { get; init; }
}
