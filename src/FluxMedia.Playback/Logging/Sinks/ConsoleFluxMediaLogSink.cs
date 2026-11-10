using FluxMedia.Core.Configuration;

namespace FluxMedia.Core.Logging;

/// <summary>
/// 控制台日志输出实现，适合 Demo、测试和命令行工具。
/// </summary>
internal sealed class ConsoleFluxMediaLogSink : IFluxMediaLogSink
{
    /// <summary>
    /// 创建控制台输出目标。
    /// </summary>
    public ConsoleFluxMediaLogSink(LogLevel minimumLevel = LogLevel.Information, bool isEnabled = true)
    {
        MinimumLevel = minimumLevel;
        IsEnabled = isEnabled;
    }

    /// <summary>
    /// 是否启用。
    /// </summary>
    public bool IsEnabled { get; }

    /// <summary>
    /// 允许输出的最低日志级别。
    /// </summary>
    public LogLevel MinimumLevel { get; }

    /// <summary>
    /// 格式化并输出控制台日志。
    /// </summary>
    public void Write(LogLevel level, string module, Guid instanceId, string message, Exception? exception = null)
    {
        if (!IsEnabled || level < MinimumLevel)
        {
            return;
        }

        Console.WriteLine(FluxMediaLogFormatter.FormatConsoleLine(DateTimeOffset.Now, level, module, instanceId, Environment.CurrentManagedThreadId, message, exception));
    }
}

