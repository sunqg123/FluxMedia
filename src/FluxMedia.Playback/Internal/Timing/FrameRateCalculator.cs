namespace FluxMedia.Playback.Internal;

/// <summary>
/// 帧率换算辅助，避免各 worker 重复处理分母为 0 的情况。
/// </summary>
internal static class FrameRateCalculator
{
    /// <summary>
    /// 将有理数换算为帧率。
    /// </summary>
    public static double FromRational(int numerator, int denominator, double fallback = 0)
    {
        if (numerator <= 0 || denominator <= 0)
        {
            return fallback;
        }

        return numerator / (double)denominator;
    }

    /// <summary>
    /// 根据分子和分母计算帧率。
    /// </summary>
    public static double FromRationalPair(int primaryNumerator, int primaryDenominator, int fallbackNumerator, int fallbackDenominator, double fallback = 0)
    {
        var primary = FromRational(primaryNumerator, primaryDenominator);
        return primary > 0 ? primary : FromRational(fallbackNumerator, fallbackDenominator, fallback);
    }
}
