using FluxMedia.Playback.Internal.Audio;
using System.Globalization;

namespace FluxMedia.Playback.Internal;

/// <summary>
/// 统一校验倍速数值、产品范围和音频预算。
/// </summary>
internal static class PlaybackRatePolicy
{
    /// <summary>
    /// 正常播放倍速。
    /// </summary>
    internal const double Normal = 1.0;

    /// <summary>
    /// 音频变速链的资源保护上限，不允许配置覆盖。
    /// </summary>
    internal const int MaxSupportedAudioTempoStages = 16;

    /// <summary>
    /// 判断是否为正的有限数值，不承载产品范围。
    /// </summary>
    internal static bool IsSupported(double rate) => !double.IsNaN(rate) && !double.IsInfinity(rate) && rate > 0;

    /// <summary>
    /// 按数值、范围、预算的顺序生成唯一的检查结果与滤镜计划。
    /// </summary>
    internal static bool TryValidate(double rate, FluxEngineOptions options, bool needsAudio,
        out double[] stages, out string? error)
    {
        stages = Array.Empty<double>();
        if (!IsSupported(rate))
            error = "播放速度必须是大于 0 的有限值。";
        else if (rate < options.MinPlaybackRate || rate > options.MaxPlaybackRate)
            error = string.Format(CultureInfo.InvariantCulture, "播放速度需在 {0} 到 {1} 之间（当前 {2}）。",
                options.MinPlaybackRate, options.MaxPlaybackRate, rate);
        else if (needsAudio && !AudioTempoPlan.TryPlan(rate, options.MaxAudioTempoStages, out stages))
            error = $"播放速度超出当前 {options.MaxAudioTempoStages} 级音频滤镜预算。";
        else
            error = null;
        return error is null;
    }

    /// <summary>
    /// 检查推算级数，超出资源上限时拒绝启动。
    /// </summary>
    internal static void ValidateOptions(FluxEngineOptions options)
    {
        var stages = options.MaxAudioTempoStages;
        if (stages > MaxSupportedAudioTempoStages)
            throw new ArgumentException(string.Format(CultureInfo.InvariantCulture,
                "倍速范围 {0}～{1} 需要 {2} 级音频滤镜，超过上限 {3}，请缩小倍速范围。",
                options.MinPlaybackRate, options.MaxPlaybackRate, stages, MaxSupportedAudioTempoStages), nameof(options));
    }

    /// <summary>
    /// 检查范围配置，供启动与只读级数属性共用。
    /// </summary>
    internal static void ValidateRange(FluxEngineOptions options)
    {
        if (!IsSupported(options.MinPlaybackRate) || options.MinPlaybackRate > Normal)
            throw new ArgumentOutOfRangeException(nameof(options.MinPlaybackRate), options.MinPlaybackRate,
                "倍速范围配置无效：MinPlaybackRate 必须为大于 0 且不超过 1 的有限值，范围必须包含 1 倍回退值。");
        if (!IsSupported(options.MaxPlaybackRate) || options.MaxPlaybackRate < Normal)
            throw new ArgumentOutOfRangeException(nameof(options.MaxPlaybackRate), options.MaxPlaybackRate,
                "倍速范围配置无效：MaxPlaybackRate 必须为不低于 1 的有限值，范围必须包含 1 倍回退值。");
    }

    /// <summary>
    /// 时钟仅修正非法数值，产品范围由命令入口检查。
    /// </summary>
    internal static double Normalize(double rate) => IsSupported(rate) ? rate : Normal;
}
