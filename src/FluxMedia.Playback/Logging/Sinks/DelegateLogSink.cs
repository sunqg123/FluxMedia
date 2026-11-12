using FluxMedia.Core.Configuration;

namespace FluxMedia.Core.Logging;

/// <summary>
/// 在静态入口内部适配宿主委托。
/// </summary>
internal sealed class DelegateLogSink : IFluxMediaLogSink
{
    /// <summary>
    /// 宿主提供的日志回调。
    /// </summary>
    private readonly Action<LogLevel, string, Guid, string, Exception?> _writer;

    /// <summary>
    /// 保存输出回调，不持有宿主资源的释放所有权。
    /// </summary>
    public DelegateLogSink(Action<LogLevel, string, Guid, string, Exception?> writer) => _writer = writer;

    /// <summary>
    /// 将日志交给宿主回调。
    /// </summary>
    public void Write(
        LogLevel level,
        string module,
        Guid instanceId,
        string message,
        Exception? exception) => _writer(level, module, instanceId, message, exception);
}
