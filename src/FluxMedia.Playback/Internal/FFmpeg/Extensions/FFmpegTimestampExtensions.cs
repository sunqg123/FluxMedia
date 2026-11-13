using FFmpeg.AutoGen;

namespace FluxMedia.FFmpeg.Extensions;

/// <summary>
/// FFmpeg 时间基换算。
/// </summary>
internal static class FFmpegTimestampExtensions
{
    /// <summary>
    /// 换算有效时间戳，并扣除媒体起始偏移。
    /// </summary>
    public static bool TryGetMediaTime(this AVRational timeBase, long pts, TimeSpan timestampOrigin, out TimeSpan timestamp)
    {
        timestamp = TimeSpan.Zero;
        if (pts < 0)
            return false;

        var seconds = pts * ffmpeg.av_q2d(timeBase);
        if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0)
            return false;

        var value = TimeSpan.FromSeconds(seconds) - timestampOrigin;
        timestamp = value < TimeSpan.Zero ? TimeSpan.Zero : value;
        return true;
    }
}
