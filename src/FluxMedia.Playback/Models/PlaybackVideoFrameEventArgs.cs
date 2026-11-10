using FluxMedia.Core.Models;

namespace FluxMedia.Playback;

/// <summary>
/// 视频帧通知；跨回调使用时需保留帧租约。
/// </summary>
/// <param name="Frame">本次通知包含的视频帧。</param>
public sealed record PlaybackVideoFrameEventArgs(VideoFrame Frame);
