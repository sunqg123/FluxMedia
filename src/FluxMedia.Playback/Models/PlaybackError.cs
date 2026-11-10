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
