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
    internal VideoFrameEpoch? FrameEpoch
    {
        get; init;
    }
}
