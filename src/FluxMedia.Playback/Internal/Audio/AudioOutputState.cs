namespace FluxMedia.Playback.Internal.Audio;

// 当前项目承载统一音频输出抽象、Windows NAudio 后端和 Linux OpenAL 后端。

/// <summary>
/// 播放器音频输出设备状态。
/// </summary>
internal enum AudioOutputState
{
    /// <summary>
    /// 已创建。
    /// </summary>
    Created,
    /// <summary>
    /// 已初始化。
    /// </summary>
    Initialized,
    /// <summary>
    /// 播放中。
    /// </summary>
    Playing,
    /// <summary>
    /// 已暂停。
    /// </summary>
    Paused,
    /// <summary>
    /// 已停止。
    /// </summary>
    Stopped,
    /// <summary>
    /// 失败。
    /// </summary>
    Failed,
    /// <summary>
    /// 已释放。
    /// </summary>
    Disposed,
    /// <summary>
    /// 不可用。
    /// </summary>
    Unavailable
}
