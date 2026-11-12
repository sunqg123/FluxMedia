using FluxMedia.Core.Models;

namespace FluxMedia.Playback.Internal;

/// <summary>
/// 共享一代管线的媒体信息与实时控制入口。
/// </summary>
internal sealed class PlaybackWorkerContext
{
    /// <summary>
    /// 绑定本代管线的依赖，不接管资源释放。
    /// </summary>
    public PlaybackWorkerContext(
        MediaSource source,
        Guid instanceId,
        TimeSpan startPosition,
        PlaybackTimingMode timingMode,
        Func<bool> isPaused,
        Func<double> playbackRate,
        Func<PlaybackResyncRequest> resync)
    {
        Source = source ?? throw new ArgumentNullException(nameof(source));
        InstanceId = instanceId;
        StartPosition = startPosition;
        TimingMode = timingMode;
        IsPaused = isPaused ?? throw new ArgumentNullException(nameof(isPaused));
        PlaybackRate = playbackRate ?? throw new ArgumentNullException(nameof(playbackRate));
        Resync = resync ?? throw new ArgumentNullException(nameof(resync));
    }

    /// <summary>
    /// 媒体源。
    /// </summary>
    public MediaSource Source { get; }

    /// <summary>
    /// 播放器标识。
    /// </summary>
    public Guid InstanceId { get; }

    /// <summary>
    /// 管线起始位置。
    /// </summary>
    public TimeSpan StartPosition { get; }

    /// <summary>
    /// 从已打开媒体继承的基础时序模式。
    /// </summary>
    public PlaybackTimingMode TimingMode { get; }

    /// <summary>
    /// 读取当前暂停状态。
    /// </summary>
    public Func<bool> IsPaused { get; }

    /// <summary>
    /// 读取当前倍速。
    /// </summary>
    public Func<double> PlaybackRate { get; }

    /// <summary>
    /// 读取最新重同步请求。
    /// </summary>
    public Func<PlaybackResyncRequest> Resync { get; }
}
