namespace FluxMedia.Playback.Internal;

/// <summary>
/// 统一倍速范围和常用档位。
/// </summary>
internal static class PlaybackRatePolicy
{
    /// <summary>
    /// 最低播放倍速。
    /// </summary>
    internal const double Minimum = 0.5;

    /// <summary>
    /// 最高播放倍速。
    /// </summary>
    internal const double Maximum = 2.0;

    /// <summary>
    /// 正常播放倍速。
    /// </summary>
    internal const double Normal = 1.0;

    /// <summary>
    /// 常用倍速，范围内也允许自定义值。
    /// </summary>
    internal static IReadOnlyList<double> Presets { get; } = Array.AsReadOnly(new[] { 0.5, 0.75, 1.0, 1.25, 1.5, 2.0 });

    /// <summary>
    /// 只接受有限的 0.5 到 2 倍速。
    /// </summary>
    internal static bool IsSupported(double rate) => !double.IsNaN(rate) && !double.IsInfinity(rate) && rate >= Minimum && rate <= Maximum;
    /// <summary>
    /// 无效初始值使用正常倍速。
    /// </summary>
    internal static double Normalize(double rate) => IsSupported(rate) ? rate : Normal;
}
