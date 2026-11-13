using FluxMedia.Playback.Internal;
using FluxMedia.Playback.Internal.Audio;

namespace FluxMedia.Playback;

/// <summary>
/// 引擎启动所需的原生环境与共享播放策略。
/// </summary>
public sealed record FluxEngineOptions
{
    /// <summary>
    /// FFmpeg 共享库目录。
    /// </summary>
    public string? FFmpegPath
    {
        get; init;
    }

    /// <summary>
    /// 是否启用 LogConverge 文件日志，进程内首次初始化生效。
    /// </summary>
    public bool EnableLogging
    {
        get; init;
    }

    /// <summary>
    /// 全部播放器允许的最低倍速，必须大于 0 且不超过 1。
    /// </summary>
    public double MinPlaybackRate { get; init; } = 0.1;

    /// <summary>
    /// 全部播放器允许的最高倍速，必须不低于 1。
    /// </summary>
    public double MaxPlaybackRate { get; init; } = 10.0;

    /// <summary>
    /// 自动推算音频级数；范围无效时抛出配置异常，无需设置。
    /// </summary>
    public int MaxAudioTempoStages
    {
        get
        {
            PlaybackRatePolicy.ValidateRange(this);
            return Math.Max(
                AudioTempoPlan.GetRequiredStageCount(MinPlaybackRate),
                AudioTempoPlan.GetRequiredStageCount(MaxPlaybackRate));
        }
    }
}
