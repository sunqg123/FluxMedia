using FluxMedia.Core.Models;

namespace FluxMedia.Playback;

/// <summary>
/// 打开媒体源的请求。
/// </summary>
/// <param name="Source">需要打开的媒体源。</param>
/// <param name="Options">本次打开媒体采用的播放配置。</param>
public sealed record PlaybackOpenRequest(MediaSource Source, PlaybackOptions? Options = null);
