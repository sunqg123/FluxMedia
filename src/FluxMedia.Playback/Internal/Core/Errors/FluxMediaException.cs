namespace FluxMedia.Core.Errors;

/// <summary>
/// FluxMedia 统一异常类型，内部携带结构化错误信息。
/// </summary>
public sealed class FluxMediaException : Exception
{
    /// <summary>
    /// 初始化 Flux 媒体异常所需的状态和资源。
    /// </summary>
    public FluxMediaException(FluxMediaError error, Exception? innerException = null) : base(error.Message, innerException)
    {
        Error = error;
    }

    /// <summary>
    /// 错误。
    /// </summary>
    public FluxMediaError Error
    {
        get;
    }
}
