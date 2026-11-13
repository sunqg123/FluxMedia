using FluxMedia.Core.Models;

namespace FluxMedia.Playback.Internal;

internal sealed partial class PlaybackPipeline
{
    /// <summary>
    /// 通过管线创建时的来源和配置确认媒体归属。
    /// </summary>
    public bool MatchesDiagnosticMedia(MediaSource? source, PlaybackOptions options)
        => ReferenceEquals(_source, source) && ReferenceEquals(_options, options);

    /// <summary>
    /// 按需读取设备状态，观察失败时返回不可用。
    /// </summary>
    public string? ReadAudioDeviceState()
    {
        try { return _audioOutput?.State.ToString(); }
        catch (ObjectDisposedException) { return null; }
    }
}
