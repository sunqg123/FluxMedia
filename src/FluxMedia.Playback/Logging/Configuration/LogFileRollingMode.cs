namespace FluxMedia.Core.Configuration;

/// <summary>
/// 日志文件生成方式。
/// </summary>
public enum LogFileRollingMode
{
    /// <summary>
    /// 按天。
    /// </summary>
    Daily,
    /// <summary>
    /// 每次运行。
    /// </summary>
    PerRun
}
