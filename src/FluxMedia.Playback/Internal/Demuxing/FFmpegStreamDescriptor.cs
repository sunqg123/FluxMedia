using FFmpeg.AutoGen;
using FluxMedia.FFmpeg.Native;

namespace FluxMedia.FFmpeg.Pipeline;

/// <summary>
/// 保存流参数和解码器所需的副本。
/// </summary>
internal sealed unsafe class FFmpegStreamDescriptor : IDisposable
{
    /// <summary>
    /// 复制保存的原生编解码参数指针。
    /// </summary>
    private AVCodecParameters* _codecParameters;

    /// <summary>
    /// 是否已释放资源。
    /// </summary>
    private int _disposed;

    /// <summary>
    /// 初始化 FFmpeg 流描述所需的状态和资源。
    /// </summary>
    public FFmpegStreamDescriptor(int streamIndex, AVMediaType mediaType, AVRational timeBase, AVRational averageFrameRate, AVRational realFrameRate, TimeSpan startTime, AVCodecParameters* sourceCodecParameters)
    {
        StreamIndex = streamIndex;
        MediaType = mediaType;
        TimeBase = timeBase;
        AverageFrameRate = averageFrameRate;
        RealFrameRate = realFrameRate;
        StartTime = startTime;
        _codecParameters = ffmpeg.avcodec_parameters_alloc();
        if (_codecParameters == null)
        {
            throw new InvalidOperationException("FFmpeg codec parameters allocation failed.");
        }

        FFmpegErrorHelper.ThrowIfError(
            ffmpeg.avcodec_parameters_copy(_codecParameters, sourceCodecParameters), "avcodec_parameters_copy");
    }

    /// <summary>
    /// 流索引。
    /// </summary>
    public int StreamIndex
    {
        get;
    }

    /// <summary>
    /// 媒体类型。
    /// </summary>
    public AVMediaType MediaType
    {
        get;
    }

    /// <summary>
    /// 时间基。
    /// </summary>
    public AVRational TimeBase
    {
        get;
    }

    /// <summary>
    /// 平均帧率。
    /// </summary>
    public AVRational AverageFrameRate
    {
        get;
    }

    /// <summary>
    /// 实际帧率。
    /// </summary>
    public AVRational RealFrameRate
    {
        get;
    }

    /// <summary>
    /// 起始时间。
    /// </summary>
    public TimeSpan StartTime
    {
        get;
    }

    /// <summary>
    /// 编解码参数。
    /// </summary>
    public AVCodecParameters* CodecParameters
    {
        get
        {
            if (_codecParameters == null || _disposed != 0)
            {
                throw new ObjectDisposedException(nameof(FFmpegStreamDescriptor));
            }
            return _codecParameters;
        }
    }

    /// <summary>
    /// 释放当前对象持有的资源。
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        var parameters = _codecParameters;
        _codecParameters = null;
        if (parameters != null)
        {
            ffmpeg.avcodec_parameters_free(&parameters);
        }
    }
}
