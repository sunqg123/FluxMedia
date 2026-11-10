namespace FluxMedia.Core.Models;

/// <summary>
/// 媒体信息摘要。
/// </summary>
/// <param name="Duration">媒体总时长。</param>
/// <param name="Codecs">媒体包含的编解码器信息。</param>
/// <param name="Metadata">媒体容器中的元数据键值。</param>
public sealed record MediaInfo(TimeSpan Duration, IReadOnlyList<CodecInfo> Codecs, IReadOnlyDictionary<string, string> Metadata)
{
    /// <summary>
    /// 是否包含视频。
    /// </summary>
    public bool HasVideo { get; init; }

    /// <summary>
    /// 是否包含音频。
    /// </summary>
    public bool HasAudio { get; init; }

    /// <summary>
    /// 视频宽度。
    /// </summary>
    public int? VideoWidth { get; init; }

    /// <summary>
    /// 视频高度。
    /// </summary>
    public int? VideoHeight { get; init; }

    /// <summary>
    /// 视频帧速率。
    /// </summary>
    public double? VideoFrameRate { get; init; }

    /// <summary>
    /// 视频编解码器。
    /// </summary>
    public string? VideoCodec { get; init; }

    /// <summary>
    /// 容器格式。
    /// </summary>
    public string? ContainerFormat { get; init; }

    /// <summary>
    /// 音频采样率。
    /// </summary>
    public int? AudioSampleRate { get; init; }

    /// <summary>
    /// 音频声道。
    /// </summary>
    public int? AudioChannels { get; init; }

    /// <summary>
    /// 音频编解码器。
    /// </summary>
    public string? AudioCodec { get; init; }
}
