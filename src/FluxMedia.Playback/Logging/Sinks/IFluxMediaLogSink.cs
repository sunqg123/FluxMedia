using FluxMedia.Core.Configuration;

namespace FluxMedia.Core.Logging;

/// <summary>
/// 静态入口内部使用的输出目标，不参与播放器构造和依赖注入。
/// </summary>
internal interface IFluxMediaLogSink
{
    /// <summary>
    /// 输出一条包含模块和会话标识的日志。
    /// </summary>
    void Write(LogLevel level, string module, Guid instanceId, string message, Exception? exception);

    /// <summary>
    /// 刷新尚未输出的记录。
    /// </summary>
    void Flush() { }
}
