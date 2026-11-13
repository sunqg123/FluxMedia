namespace FluxMedia.Playback.Internal;

/// <summary>
/// 跳转采用的实际执行路径。
/// </summary>
internal enum PlaybackSeekPath
{
    /// <summary>
    /// 尚未执行定位。
    /// </summary>
    None,
    /// <summary>
    /// 复用解复用器定位。
    /// </summary>
    Demuxer,
    /// <summary>
    /// 重建播放管线。
    /// </summary>
    FullRestart,
    /// <summary>
    /// 首帧超时后重建恢复。
    /// </summary>
    FullRestartRecovery,
    /// <summary>
    /// 定位到媒体结束位置。
    /// </summary>
    EndOfMedia
}
