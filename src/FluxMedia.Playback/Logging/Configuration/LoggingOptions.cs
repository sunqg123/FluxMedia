namespace FluxMedia.Core.Configuration;

/// <summary>
/// 日志配置。
/// </summary>
public sealed record LoggingOptions
{
    /// <summary>
    /// 是否启用文件日志。
    /// </summary>
    public bool IsEnabled { get; init; } = true;

    /// <summary>
    /// 允许输出的最低日志级别。
    /// </summary>
    public LogLevel MinimumLevel { get; init; } = LogLevel.Information;

    /// <summary>
    /// 文件日志目录，相对路径以程序目录为基准。
    /// </summary>
    public string Directory { get; init; } = "Logs";

    /// <summary>
    /// 滚动模式。
    /// </summary>
    public LogFileRollingMode RollingMode { get; init; } = LogFileRollingMode.Daily;

    /// <summary>
    /// 文件前缀。
    /// </summary>
    public string FilePrefix { get; init; } = string.Empty;

    /// <summary>
    /// 后台队列容量，最少 128 条，满载丢弃新记录。
    /// </summary>
    public int QueueCapacity { get; init; } = 4096;

    /// <summary>
    /// 后台空闲时自动刷新文件的间隔。
    /// </summary>
    public TimeSpan FlushInterval { get; init; } = TimeSpan.FromSeconds(1);
}
