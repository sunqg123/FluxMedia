namespace FluxMedia.Playback;

/// <summary>
/// 播放操作结果。
/// </summary>
/// <param name="IsSuccess">操作是否成功。</param>
/// <param name="Error">操作错误类型；取消也可表示所属管线已退役。</param>
/// <param name="Message">当前事件或操作的说明。</param>
public sealed record PlaybackResult(bool IsSuccess, PlaybackError Error = PlaybackError.None, string? Message = null)
{
    /// <summary>
    /// 是否仅接受请求，尚未应用；失败结果不会标记为待应用。
    /// </summary>
    public bool IsDeferred
    {
        get => IsSuccess && _isDeferred;
        init => _isDeferred = value;
    }

    /// <summary>
    /// 保存成功结果的延迟应用标记。
    /// </summary>
    private readonly bool _isDeferred;

    /// <summary>
    /// 请求已接受，实际应用通过播放器状态通知。
    /// </summary>
    public static PlaybackResult Accepted() => new(true) { IsDeferred = true };

    /// <summary>
    /// 创建成功的播放操作结果。
    /// </summary>
    public static PlaybackResult Success() => new(true);

    /// <summary>
    /// 创建失败的播放操作结果。
    /// </summary>
    public static PlaybackResult Failure(PlaybackError error, string message) => new(false, error, message);
}
