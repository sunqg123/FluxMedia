using System.Globalization;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen;
using FluxMedia.FFmpeg.Native;

namespace FluxMedia.Playback.Internal.Audio;

/// <summary>
/// 接收变速后的交错 PCM。
/// </summary>
internal delegate void TempoPcmConsumer(ReadOnlySpan<byte> pcm, int samples, double rate);

/// <summary>
/// 用 atempo 保持音调，并统一输出 PCM 格式。
/// </summary>
internal sealed unsafe class FFmpegAudioTempo : IDisposable
{
    /// <summary>
    /// 当前音频滤镜图。
    /// </summary>
    private AVFilterGraph* _graph;
    /// <summary>
    /// 解码帧输入端。
    /// </summary>
    private AVFilterContext* _source;
    /// <summary>
    /// PCM 输出端。
    /// </summary>
    private AVFilterContext* _sink;
    /// <summary>
    /// 循环复用的输出帧。
    /// </summary>
    private AVFrame* _output;
    /// <summary>
    /// PCM 输出声道数。
    /// </summary>
    private readonly int _channels;
    /// <summary>
    /// 当前实际使用的倍速。
    /// </summary>
    private double _rate;

    /// <summary>
    /// 建立解码帧到变速 PCM 的滤镜链。
    /// </summary>
    public FFmpegAudioTempo(AVCodecContext* codec, int sampleRate, int channels, double rate)
    {
        _channels = channels;
        _rate = rate;
        try
        {
            _graph = ffmpeg.avfilter_graph_alloc();
            _output = ffmpeg.av_frame_alloc();
            if (_graph == null || _output == null) throw new OutOfMemoryException("无法创建音频变速器。");
            _graph->nb_threads = 1;
            byte* layout = stackalloc byte[256];
            AVChannelLayout inputLayout = default;
            string? layoutName;
            try
            {
                if (codec->ch_layout.order == AVChannelOrder.AV_CHANNEL_ORDER_UNSPEC)
                {
                    ffmpeg.av_channel_layout_default(&inputLayout, Math.Max(1, codec->ch_layout.nb_channels));
                }
                else
                {
                    FFmpegErrorHelper.ThrowIfError(ffmpeg.av_channel_layout_copy(&inputLayout, &codec->ch_layout), "audio channel layout copy");
                }
                FFmpegErrorHelper.ThrowIfError(ffmpeg.av_channel_layout_describe(&inputLayout, layout, 256), "audio channel layout");
                layoutName = Marshal.PtrToStringAnsi((nint)layout);
            }
            finally
            {
                ffmpeg.av_channel_layout_uninit(&inputLayout);
            }
            var format = ffmpeg.av_get_sample_fmt_name(codec->sample_fmt);
            _source = Create("abuffer", "input", $"time_base=1/{sampleRate}:sample_rate={sampleRate}:sample_fmt={format}:channel_layout={layoutName}");
            var tempo = Create("atempo", "tempo", rate.ToString(CultureInfo.InvariantCulture));
            var pcm = Create("aformat", "pcm", $"sample_fmts=s16:sample_rates={sampleRate}:channel_layouts={(channels == 1 ? "mono" : "stereo")}");
            _sink = Create("abuffersink", "output", null);
            FFmpegErrorHelper.ThrowIfError(ffmpeg.avfilter_link(_source, 0, tempo, 0), "audio input link");
            FFmpegErrorHelper.ThrowIfError(ffmpeg.avfilter_link(tempo, 0, pcm, 0), "audio tempo link");
            FFmpegErrorHelper.ThrowIfError(ffmpeg.avfilter_link(pcm, 0, _sink, 0), "audio output link");
            FFmpegErrorHelper.ThrowIfError(ffmpeg.avfilter_graph_config(_graph, null), "audio filter config");
        }
        catch { Dispose(); throw; }
    }

    /// <summary>
    /// 创建并初始化单个滤镜。
    /// </summary>
    private AVFilterContext* Create(string filterName, string name, string? options)
    {
        var filter = ffmpeg.avfilter_get_by_name(filterName);
        if (filter == null) throw new NotSupportedException($"FFmpeg 缺少 {filterName} 音频滤镜。");
        AVFilterContext* context = null;
        FFmpegErrorHelper.ThrowIfError(ffmpeg.avfilter_graph_create_filter(&context, filter, name, options, null, _graph), filterName);
        return context;
    }

    /// <summary>
    /// 变更倍速后提交一帧，不改变设备采样率。
    /// </summary>
    public void Push(AVFrame* frame, double rate, TempoPcmConsumer consume)
    {
        if (_rate != rate)
        {
            FFmpegErrorHelper.ThrowIfError(ffmpeg.avfilter_graph_send_command(_graph, "tempo", "tempo", rate.ToString(CultureInfo.InvariantCulture), null, 0, 0), "audio tempo command");
            _rate = rate;
        }
        FFmpegErrorHelper.ThrowIfError(ffmpeg.av_buffersrc_add_frame_flags(_source, frame, (int)AvBuffersrcFlag.AV_BUFFERSRC_FLAG_KEEP_REF), "audio filter input");
        Drain(consume);
    }

    /// <summary>
    /// 结束输入并输出滤镜中的尾音。
    /// </summary>
    public void Complete(TempoPcmConsumer consume)
    {
        FFmpegErrorHelper.ThrowIfError(ffmpeg.av_buffersrc_add_frame_flags(_source, null, 0), "audio filter finish");
        Drain(consume);
    }

    /// <summary>
    /// 输出所有已就绪的 PCM，等待更多输入时返回。
    /// </summary>
    private void Drain(TempoPcmConsumer consume)
    {
        while (true)
        {
            var result = ffmpeg.av_buffersink_get_frame(_sink, _output);
            if (result == ffmpeg.AVERROR(ffmpeg.EAGAIN) || result == ffmpeg.AVERROR_EOF) return;
            FFmpegErrorHelper.ThrowIfError(result, "audio filter output");
            try
            {
                consume(new ReadOnlySpan<byte>(_output->data[0], checked(_output->nb_samples * _channels * sizeof(short))), _output->nb_samples, _rate);
            }
            finally
            {
                ffmpeg.av_frame_unref(_output);
            }
        }
    }

    /// <summary>
    /// 释放滤镜和复用帧。
    /// </summary>
    public void Dispose()
    {
        var graph = _graph;
        var output = _output;
        _graph = null;
        _output = null;
        _source = null;
        _sink = null;
        if (graph != null) ffmpeg.avfilter_graph_free(&graph);
        if (output != null) ffmpeg.av_frame_free(&output);
    }
}
