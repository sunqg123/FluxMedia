namespace FluxMedia.Playback;

/// <summary>
/// 播放位置及媒体总时长。
/// </summary>
/// <param name="Position">当前媒体位置。</param>
/// <param name="Duration">媒体总时长。</param>
public sealed record PlaybackPositionChangedEventArgs(TimeSpan Position, TimeSpan? Duration);
