namespace FluxMedia.Core.Configuration;

/// <summary>
/// 日志级别。
/// </summary>
public enum LogLevel
{
    /// <summary>
    /// 跟踪。
    /// </summary>
    Trace,
    /// <summary>
    /// 调试。
    /// </summary>
    Debug,
    /// <summary>
    /// 信息。
    /// </summary>
    Information,
    /// <summary>
    /// 警告。
    /// </summary>
    Warning,
    /// <summary>
    /// 错误。
    /// </summary>
    Error,
    /// <summary>
    /// 严重。
    /// </summary>
    Critical,
    /// <summary>
    /// 不输出日志。
    /// </summary>
    None
}
