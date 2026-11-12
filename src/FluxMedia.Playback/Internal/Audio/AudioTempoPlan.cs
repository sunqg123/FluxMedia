namespace FluxMedia.Playback.Internal.Audio;

/// <summary>
/// 按资源预算切分音频倍速，检查与建图共用结果。
/// </summary>
internal static class AudioTempoPlan
{
    /// <summary>
    /// 按实际切分规则计算所需级数，避免对数取整误差。
    /// </summary>
    public static int GetRequiredStageCount(double rate)
    {
        if (!PlaybackRatePolicy.IsSupported(rate))
            throw new ArgumentOutOfRangeException(nameof(rate), rate, "播放速度必须是大于 0 的有限值。");
        var count = 0;
        var remaining = rate;
        while (remaining != 1.0)
        {
            remaining /= GetStageRate(remaining);
            count++;
        }
        return count;
    }

    /// <summary>
    /// 生成单级 0.5～2 的滤镜链，正常速度不需要变速滤镜。
    /// </summary>
    public static bool TryPlan(double rate, int budget, out double[] stages)
    {
        stages = Array.Empty<double>();
        if (!PlaybackRatePolicy.IsSupported(rate) || budget < 0 || budget > PlaybackRatePolicy.MaxSupportedAudioTempoStages) return false;
        var plan = new List<double>(Math.Min(budget, 8));
        var remaining = rate;
        while (remaining != 1.0)
        {
            if (plan.Count >= budget) return false;
            var stage = GetStageRate(remaining);
            plan.Add(stage);
            remaining /= stage;
        }
        stages = plan.ToArray();
        return true;
    }

    /// <summary>
    /// 单级滤镜采用 0.5～2 倍，计数和建图共用此规则。
    /// </summary>
    private static double GetStageRate(double remaining) => Math.Max(0.5, Math.Min(2.0, remaining));
}
