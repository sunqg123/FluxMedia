using FluxMedia.Core.Configuration;
using FluxMedia.Core.Errors;
using FluxMedia.Core.Models;

namespace FluxMedia.Playback;

/// <summary>
/// 播放位置及媒体总时长。
/// </summary>
/// <param name="Position">当前媒体位置。</param>
/// <param name="Duration">媒体总时长。</param>
public sealed record PlaybackPositionChangedEventArgs(TimeSpan Position, TimeSpan? Duration);


/// <summary>
/// 视频帧通知；跨回调使用时需保留帧租约。
/// </summary>
/// <param name="Frame">本次通知包含的视频帧。</param>
public sealed record PlaybackVideoFrameEventArgs(VideoFrame Frame);
