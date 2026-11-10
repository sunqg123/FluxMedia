namespace FluxMedia.Playback;

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
