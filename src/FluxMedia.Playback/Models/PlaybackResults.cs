using FluxMedia.Core.Configuration;
using FluxMedia.Core.Errors;
using FluxMedia.Core.Models;

namespace FluxMedia.Playback;

/// <summary>
/// 播放操作的错误类型。
/// </summary>
public enum PlaybackError
{
    /// <summary>
    /// 无错误。
    /// </summary>
    None,
    /// <summary>
    /// 无效状态。
    /// </summary>
    InvalidState,
    /// <summary>
    /// 打开失败。
    /// </summary>
    OpenFailed,
    /// <summary>
    /// 播放失败。
    /// </summary>
    PlayFailed,
    /// <summary>
    /// 跳转失败。
    /// </summary>
    SeekFailed,
    /// <summary>
    /// 当前媒体不支持跳转。
    /// </summary>
    NotSeekable,
    /// <summary>
    /// 播放速度不可支持。
    /// </summary>
    PlaybackRateNotSupported,
    /// <summary>
    /// 录制失败。
    /// </summary>
    RecordingFailed,
    /// <summary>
    /// 操作已取消。
    /// </summary>
    OperationCanceled,
    /// <summary>
    /// 资源已释放。
    /// </summary>
    ResourceDisposed
}


/// <summary>
/// 播放操作结果。
/// </summary>
/// <param name="IsSuccess">操作是否成功。</param>
/// <param name="Error">本次播放操作的错误类型。</param>
/// <param name="Message">当前事件或操作的说明。</param>
public sealed record PlaybackResult(bool IsSuccess, PlaybackError Error = PlaybackError.None, string? Message = null)
{
    /// <summary>
    /// 创建成功的播放操作结果。
    /// </summary>
    public static PlaybackResult Success() => new(true);

    /// <summary>
    /// 创建失败的播放操作结果。
    /// </summary>
    public static PlaybackResult Failure(PlaybackError error, string message) => new(false, error, message);
}


/// <summary>
/// Playback 包专用异常。
/// </summary>
public sealed class PlaybackException : Exception
{
    /// <summary>
    /// 初始化播放异常所需的状态和资源。
    /// </summary>
    public PlaybackException(PlaybackError playbackError, FluxMediaError error, Exception? innerException = null)
        : base(error.Message, innerException)
    {
        PlaybackError = playbackError;
        Error = error;
    }

    /// <summary>
    /// 播放错误。
    /// </summary>
    public PlaybackError PlaybackError { get; }

    /// <summary>
    /// 错误。
    /// </summary>
    public FluxMediaError Error { get; }
}
