using System.Threading.Channels;

namespace FluxMedia.FFmpeg.Pipeline;

/// <summary>
/// 读取数据包，并在正常结束时触发一次解码器排空。
/// </summary>
internal static class FFmpegPacketReader
{
    /// <summary>
    /// 仅在正常 EOF 后返回空包；取消或读取失败时直接退出。
    /// </summary>
    public static IEnumerable<FFmpegPacketLease?> ReadWithDrain(ChannelReader<FFmpegPacketLease> packets, Func<bool> isDisposed, CancellationToken cancellationToken)
    {
        while (!isDisposed() && !cancellationToken.IsCancellationRequested)
        {
            if (!packets.WaitToReadAsync(cancellationToken).AsTask().GetAwaiter().GetResult())
            {
                if (!isDisposed() && !cancellationToken.IsCancellationRequested) yield return null;
                yield break;
            }

            while (!isDisposed() && !cancellationToken.IsCancellationRequested && packets.TryRead(out var packet))
                yield return packet;
        }
    }
}
