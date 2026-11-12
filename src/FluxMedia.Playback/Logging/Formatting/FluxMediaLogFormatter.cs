using FluxMedia.Core.Configuration;
using System.Globalization;
using System.Text;

namespace FluxMedia.Core.Logging;

/// <summary>
/// 统一文件和控制台的日志格式。
/// </summary>
internal static class FluxMediaLogFormatter
{
    /// <summary>
    /// 生成统一日志记录，异常堆栈另行缩进。
    /// </summary>
    public static string FormatLine(
        DateTimeOffset timestamp,
        LogLevel level,
        string module,
        Guid instanceId,
        int threadId,
        string message,
        Exception? exception)
    {
        var builder = new StringBuilder();
        builder.Append(timestamp.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture))
            .Append(" [").Append(FormatLevel(level)).Append(']')
            .Append(" [").Append(EscapeLineBreaks(module)).Append(']')
            .Append(" SessionId=").Append(instanceId == Guid.Empty ? "-" : instanceId.ToString("D"))
            .Append(" ThreadId=").Append(threadId.ToString(CultureInfo.InvariantCulture))
            .Append(' ');

        var text = message?.Trim() ?? string.Empty;
        if (text.StartsWith("Event=", StringComparison.OrdinalIgnoreCase))
        {
            builder.Append(EscapeLineBreaks(text));
        }
        else
        {
            builder.Append("Event=Message Message=").Append(Quote(text));
        }
        builder.AppendLine();

        if (exception is not null)
        {
            // 保留异常类型、内部异常和完整堆栈，避免重复拼接异常说明。
            var exceptionText = exception.ToString().TrimEnd('\r', '\n');
            foreach (var line in exceptionText.Split('\n'))
            {
                builder.Append("    ").AppendLine(line.TrimEnd('\r'));
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// 生成控制台文本，移除末尾换行。
    /// </summary>
    public static string FormatConsoleLine(
        DateTimeOffset timestamp,
        LogLevel level,
        string module,
        Guid instanceId,
        int threadId,
        string message,
        Exception? exception)
    {
        return FormatLine(timestamp, level, module, instanceId, threadId, message, exception)
            .TrimEnd('\r', '\n');
    }

    /// <summary>
    /// 转义换行和制表符，保持事件文本单行。
    /// </summary>
    private static string EscapeLineBreaks(string? value) => (value ?? string.Empty)
        .Replace("\r", "\\r")
        .Replace("\n", "\\n")
        .Replace("\t", "\\t");

    /// <summary>
    /// 为说明文本添加引号并转义特殊字符。
    /// </summary>
    private static string Quote(string value)
    {
        var escaped = value.Replace("\\", "\\\\").Replace("\"", "\\\"");
        return "\"" + EscapeLineBreaks(escaped) + "\"";
    }

    /// <summary>
    /// 将日志级别转换为显示名称。
    /// </summary>
    private static string FormatLevel(LogLevel level) => level switch
    {
        LogLevel.Trace => "Trace",
        LogLevel.Debug => "Debug",
        LogLevel.Information => "Info",
        LogLevel.Warning => "Warning",
        LogLevel.Error => "Error",
        LogLevel.Critical => "Critical",
        _ => level.ToString()
    };
}
