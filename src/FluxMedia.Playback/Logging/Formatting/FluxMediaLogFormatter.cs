using FluxMedia.Core.Configuration;
using System.Text;

namespace FluxMedia.Core.Logging;

/// <summary>
/// 将日志记录格式化为统一文本。
/// </summary>
internal static class FluxMediaLogFormatter
{
    /// <summary>
    /// 生成包含时间、模块和事件的日志行。
    /// </summary>
    public static string FormatLine(DateTimeOffset timestamp, LogLevel level, string module, Guid instanceId, int threadId, string message, Exception? exception)
    {
        var parsed = ParseMessage(message);
        var builder = new StringBuilder();
        builder
            .Append(timestamp.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss.fff"))
            .Append(" [").Append(FormatLevel(level)).Append(']')
            .Append(" [").Append(module).Append("] ")
            .Append("SessionId=").Append(instanceId)
            .Append(" ThreadId=").Append(threadId)
            .Append(" Event=").Append(QuoteIfNeeded(parsed.EventName));

        if (!string.IsNullOrWhiteSpace(parsed.Parameters))
        {
            builder.Append(' ').Append(parsed.Parameters);
        }

        if (exception is not null)
        {
            builder
                .Append(" ExceptionType=").Append(exception.GetType().FullName)
                .Append(" ExceptionMessage=").Append(Quote(exception.Message))
                .AppendLine()
                .AppendLine("StackTrace:")
                .AppendLine(exception.ToString());
        }
        else
        {
            builder.AppendLine();
        }

        return builder.ToString();
    }

    /// <summary>
    /// 生成适合控制台显示的日志行。
    /// </summary>
    public static string FormatConsoleLine(DateTimeOffset timestamp, LogLevel level, string module, Guid instanceId, int threadId, string message, Exception? exception)
    {
        return FormatLine(timestamp, level, module, instanceId, threadId, message, exception).TrimEnd();
    }

    /// <summary>
    /// 拆分日志消息中的事件名和参数。
    /// </summary>
    private static ParsedLogMessage ParseMessage(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return new ParsedLogMessage("Unknown", string.Empty);
        }

        var trimmed = message.Trim();
        if (trimmed.StartsWith("Event=", StringComparison.OrdinalIgnoreCase))
        {
            var eventStart = "Event=".Length;
            var eventEnd = trimmed.IndexOfAny(new[] { ' ', '\t', ',', '，' }, eventStart);
            var explicitEventName = eventEnd < 0
                ? trimmed[eventStart..]
                : trimmed[eventStart..eventEnd];
            var explicitParameters = eventEnd < 0
                ? string.Empty
                : trimmed[eventEnd..].TrimStart(' ', '\t', ',', '，');

            return new ParsedLogMessage(
                SanitizeEventName(explicitEventName),
                NormalizeExplicitParameters(explicitParameters));
        }

        var splitIndex = FindEventSplitIndex(trimmed);
        var eventName = splitIndex > 0 ? trimmed[..splitIndex].Trim() : FirstToken(trimmed);
        var rawParameters = splitIndex > 0 ? trimmed[(splitIndex + 1)..].Trim() : trimmed[eventName.Length..].Trim();
        var parameters = NormalizeParameters(rawParameters);
        if (string.IsNullOrWhiteSpace(parameters) && !LooksLikeKeyValue(trimmed))
        {
            parameters = $"Message={Quote(trimmed)}";
        }

        return new ParsedLogMessage(SanitizeEventName(eventName), parameters);
    }

    /// <summary>
    /// 规范化显式传入的日志参数。
    /// </summary>
    private static string NormalizeExplicitParameters(string value)
    {
        return value
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Trim();
    }

    /// <summary>
    /// 查找日志事件名和参数的分隔位置。
    /// </summary>
    private static int FindEventSplitIndex(string value)
    {
        var punctuation = new[] { '。', '.', '：', ':' };
        foreach (var item in punctuation)
        {
            var index = value.IndexOf(item, StringComparison.Ordinal);
            if (index > 0)
            {
                return index;
            }
        }

        return -1;
    }

    /// <summary>
    /// 读取消息中的首个文本项。
    /// </summary>
    private static string FirstToken(string value)
    {
        var index = value.IndexOfAny(new[] { ' ', '\t', ',', '，' });
        return index > 0 ? value[..index] : value;
    }

    /// <summary>
    /// 将日志参数整理为统一格式。
    /// </summary>
    private static string NormalizeParameters(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var tokens = value
            .Replace('，', ',')
            .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
        var normalized = new List<string>();
        foreach (var token in tokens)
        {
            var part = token.Trim();
            if (string.IsNullOrWhiteSpace(part))
            {
                continue;
            }

            if (LooksLikeKeyValue(part))
            {
                normalized.Add(NormalizeKeyValue(part));
            }
            else
            {
                normalized.Add($"Message={Quote(part)}");
            }
        }

        return string.Join(' ', normalized);
    }

    /// <summary>
    /// 判断文本是否采用键值参数格式。
    /// </summary>
    private static bool LooksLikeKeyValue(string value)
    {
        return value.Contains('=', StringComparison.Ordinal) || value.Contains('：', StringComparison.Ordinal) || value.Contains(':', StringComparison.Ordinal);
    }

    /// <summary>
    /// 规范化单个日志键值参数。
    /// </summary>
    private static string NormalizeKeyValue(string value)
    {
        var index = value.IndexOf('=');
        if (index < 0)
        {
            index = value.IndexOf('：');
        }

        if (index < 0)
        {
            index = value.IndexOf(':');
        }

        if (index <= 0)
        {
            return $"Message={Quote(value)}";
        }

        var key = SanitizeKey(value[..index]);
        var rawValue = value[(index + 1)..].Trim();
        return $"{key}={QuoteIfNeeded(rawValue)}";
    }

    /// <summary>
    /// 清除日志事件名中的不适用字符。
    /// </summary>
    private static string SanitizeEventName(string value)
    {
        var trimmed = value.Trim();
        return string.IsNullOrWhiteSpace(trimmed) ? "Unknown" : trimmed.Replace(' ', '_');
    }

    /// <summary>
    /// 清除日志参数键中的不适用字符。
    /// </summary>
    private static string SanitizeKey(string value)
    {
        var builder = new StringBuilder();
        foreach (var ch in value.Trim())
        {
            builder.Append(char.IsLetterOrDigit(ch) || ch == '_' ? ch : '_');
        }

        return builder.Length == 0 ? "Value" : builder.ToString().Trim('_');
    }

    /// <summary>
    /// 文本包含特殊字符时添加引号。
    /// </summary>
    private static string QuoteIfNeeded(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "\"\"";
        }

        return value.Any(ch => char.IsWhiteSpace(ch) || ch is '"' or ';')
            ? Quote(value)
            : value;
    }

    /// <summary>
    /// 为文本添加引号并转义特殊字符。
    /// </summary>
    private static string Quote(string value)
    {
        return $"\"{value.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"";
    }

    /// <summary>
    /// 将日志级别转换为显示文本。
    /// </summary>
    private static string FormatLevel(LogLevel level) => level switch
    {
        LogLevel.Trace => "Debug",
        LogLevel.Debug => "Debug",
        LogLevel.Information => "Info",
        LogLevel.Warning => "Warning",
        LogLevel.Error => "Error",
        LogLevel.Critical => "Error",
        _ => level.ToString()
    };

    /// <summary>
    /// 拆分后的日志事件名和参数。
    /// </summary>
    /// <param name="EventName">日志事件名称。</param>
    /// <param name="Parameters">日志事件附带的参数文本。</param>
    private sealed record ParsedLogMessage(string EventName, string Parameters);
}
