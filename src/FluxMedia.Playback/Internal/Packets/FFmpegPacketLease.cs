using FFmpeg.AutoGen;

namespace FluxMedia.FFmpeg.Pipeline;

/// <summary>
/// 持有 FFmpeg 数据包并负责释放。
/// </summary>
internal sealed unsafe class FFmpegPacketLease : IDisposable
{
    /// <summary>
    /// 当前持有的 FFmpeg 原生数据包指针。
    /// </summary>
    private AVPacket* _packet;

    /// <summary>
    /// 是否已释放资源。
    /// </summary>
    private int _disposed;

    /// <summary>
    /// 初始化 FFmpeg 数据包租约所需的状态和资源。
    /// </summary>
    public FFmpegPacketLease(AVPacket* packet, long generation)
    {
        _packet = packet;
        Generation = generation;
    }

    /// <summary>
    /// 当前持有的原生数据包，释放后不可访问。
    /// </summary>
    public AVPacket* Packet
    {
        get
        {
            if (_packet == null || _disposed != 0)
            {
                throw new ObjectDisposedException(nameof(FFmpegPacketLease));
            }
            return _packet;
        }
    }

    /// <summary>
    /// 流索引。
    /// </summary>
    public int StreamIndex => Packet->stream_index;

    /// <summary>
    /// 原生读取位置切换后递增的数据包代次。
    /// </summary>
    public long Generation { get; }

    /// <summary>
    /// 克隆数据包并独立持有其引用。
    /// </summary>
    public static FFmpegPacketLease Clone(AVPacket* packet, long generation)
    {
        var clone = ffmpeg.av_packet_clone(packet);
        if (clone == null)
        {
            throw new InvalidOperationException("FFmpeg packet clone failed.");
        }
        return new FFmpegPacketLease(clone, generation);
    }

    /// <summary>
    /// 释放当前对象持有的资源。
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        var packet = _packet;
        _packet = null;
        if (packet != null)
        {
            ffmpeg.av_packet_free(&packet);
        }
    }
}
