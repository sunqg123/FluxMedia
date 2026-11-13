using FluxMedia.FFmpeg.Pipeline;
using System.Threading.Channels;

namespace FluxMedia.Playback.Internal;

/// <summary>
/// 描述一个流的数据包输入。
/// </summary>
internal sealed class FFmpegPacketInput
{
    /// <summary>
    /// 绑定本代管线的依赖，不接管资源释放。
    /// </summary>
    public FFmpegPacketInput(FFmpegStreamDescriptor stream, ChannelReader<FFmpegPacketLease> packets, Func<long> packetGeneration)
    {
        Stream = stream ?? throw new ArgumentNullException(nameof(stream));
        Packets = packets ?? throw new ArgumentNullException(nameof(packets));
        PacketGeneration = packetGeneration ?? throw new ArgumentNullException(nameof(packetGeneration));
    }

    /// <summary>
    /// 解码所需的流参数。
    /// </summary>
    public FFmpegStreamDescriptor Stream
    {
        get;
    }

    /// <summary>
    /// 数据包读取入口。
    /// </summary>
    public ChannelReader<FFmpegPacketLease> Packets
    {
        get;
    }

    /// <summary>
    /// 读取当前包代次。
    /// </summary>
    public Func<long> PacketGeneration
    {
        get;
    }
}
