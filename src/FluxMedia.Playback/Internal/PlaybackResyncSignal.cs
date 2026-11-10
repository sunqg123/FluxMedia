using FluxMedia.Core.Models;

namespace FluxMedia.Playback.Internal;

/// <summary>
/// 解码器重新同步的目标和版本。
/// </summary>
/// <param name="Version">同步版本号。</param>
/// <param name="Position">目标媒体时间。</param>
internal readonly record struct PlaybackResyncRequest(long Version, TimeSpan Position)
{
    /// <summary>
    /// 该次同步可以发布的帧代次。
    /// </summary>
    internal VideoFrameEpoch? FrameEpoch { get; init; }
}

/// <summary>
/// 原子发布音视频同步目标，并让旧帧立即失效。
/// </summary>
internal sealed class PlaybackResyncSignal
{
    /// <summary>
    /// 保护版本、位置和帧代次一起切换。
    /// </summary>
    private readonly object _sync = new();

    /// <summary>
    /// 当前同步快照。
    /// </summary>
    private PlaybackResyncRequest _request = new(0, TimeSpan.Zero) { FrameEpoch = new VideoFrameEpoch() };

    /// <summary>
    /// 原生定位前先禁止旧帧继续进入显示。
    /// </summary>
    public void InvalidateFrames()
    {
        lock (_sync) _request.FrameEpoch?.Invalidate();
    }

    /// <summary>
    /// 发布新目标并分配独立帧代次。
    /// </summary>
    public PlaybackResyncRequest Request(TimeSpan position)
    {
        lock (_sync)
        {
            _request.FrameEpoch?.Invalidate();
            _request = new PlaybackResyncRequest(_request.Version + 1, position) { FrameEpoch = new VideoFrameEpoch() };
            return _request;
        }
    }

    /// <summary>
    /// 读取不可拆分的同步快照。
    /// </summary>
    public PlaybackResyncRequest Snapshot()
    {
        lock (_sync) return _request;
    }
}
