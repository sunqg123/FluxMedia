using FluxMedia.Core.Errors;

namespace FluxMedia.Playback;

/// <summary>
/// Playback 包专用异常。
/// </summary>
public sealed class PlaybackException : Exception
{
    /// <summary>
    /// 初始化播放异常所需的状态和资源。
    /// </summary>
    public PlaybackException(PlaybackError playbackError, FluxMediaError error, Exception? innerException = null) : base(error.Message, innerException)
    {
        PlaybackError = playbackError;
        Error = error;
    }

    /// <summary>
    /// 播放错误。
    /// </summary>
    public PlaybackError PlaybackError
    {
        get;
    }

    /// <summary>
    /// 错误。
    /// </summary>
    public FluxMediaError Error
    {
        get;
    }
}
