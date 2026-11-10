using System.Runtime.InteropServices;
using FFmpeg.AutoGen;
using FluxMedia.Core.Models;
using FluxMedia.FFmpeg.Native;

namespace FluxMedia.Playback.Internal;

/// <summary>
/// FFmpeg 媒体信息读取器。
/// </summary>
internal static unsafe class FFmpegMediaInfoReader
{

    /// <summary>
    /// 根据原生流参数组装媒体信息。
    /// </summary>
    internal static MediaInfo BuildMediaInfo(AVFormatContext* formatContext)
    {
        var duration = formatContext->duration > 0            ? TimeSpan.FromSeconds(formatContext->duration / (double)ffmpeg.AV_TIME_BASE)            : TimeSpan.Zero;
        var codecs = new List<CodecInfo>();
        var metadata = new Dictionary<string, string>();
        var container = formatContext->iformat == null ? null : PtrToString(formatContext->iformat->name);
        var result = new MediaInfo(duration, codecs, metadata) { ContainerFormat = container };
        AVDictionaryEntry* tag = null;
        while ((tag = ffmpeg.av_dict_get(formatContext->metadata, "", tag, ffmpeg.AV_DICT_IGNORE_SUFFIX)) != null)
        {
            metadata[PtrToString(tag->key)] = PtrToString(tag->value);
        }
        for (var index = 0; index < formatContext->nb_streams; index++)
        {
            var stream = formatContext->streams[index];
            if (stream == null || stream->codecpar == null) continue;
            var codecParameters = stream->codecpar;
            var codec = ffmpeg.avcodec_get_name(codecParameters->codec_id);
            codecs.Add(new CodecInfo(codec));
            if (codecParameters->codec_type == AVMediaType.AVMEDIA_TYPE_VIDEO && !result.HasVideo)
            {
                result = result with
                {
                    HasVideo = true,
                    VideoWidth = codecParameters->width,
                    VideoHeight = codecParameters->height,
                    VideoFrameRate = GetFrameRate(stream),
                    VideoCodec = codec
                };
            }
            else if (codecParameters->codec_type == AVMediaType.AVMEDIA_TYPE_AUDIO && !result.HasAudio)
            {
                result = result with
                {
                    HasAudio = true,
                    AudioSampleRate = codecParameters->sample_rate,
                    AudioChannels = (int)codecParameters->ch_layout.nb_channels,
                    AudioCodec = codec
                };
            }
        }
        return result;
    }

    /// <summary>
    /// 从视频流参数计算帧率。
    /// </summary>
    private static double GetFrameRate(AVStream* stream)
    {
        return FrameRateCalculator.FromRationalPair(stream->avg_frame_rate.num, stream->avg_frame_rate.den, stream->r_frame_rate.num, stream->r_frame_rate.den);
    }

    /// <summary>
    /// 将原生字符串指针转换为托管文本。
    /// </summary>
    private static string PtrToString(byte* pointer)
    {
        return pointer == null ? string.Empty : Marshal.PtrToStringAnsi((nint)pointer) ?? string.Empty;
    }
}
