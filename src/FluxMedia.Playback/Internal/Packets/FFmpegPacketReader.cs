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
    public static IEnumerable<FFmpegPacketLease?> ReadWithDrain(ChannelReader<FFmpegPacketLease> packets, Func<bool> isDisposed, CancellationToken cancellationToken,
        Action? serviceControl = null, Func<Task>? controlSignal = null)
    {
        Task<bool>? reading = null;
        while (!isDisposed() && !cancellationToken.IsCancellationRequested)
        {
            var signal = controlSignal?.Invoke();
            serviceControl?.Invoke();
            if (isDisposed() || cancellationToken.IsCancellationRequested) yield break;
            reading ??= packets.WaitToReadAsync(cancellationToken).AsTask();
            if (!reading.IsCompleted && signal is not null)
            {
                // 控制唤醒不取消或重复创建原有的包等待。
                if (Task.WhenAny(reading, signal).GetAwaiter().GetResult() != reading) continue;
            }
            if (!reading.GetAwaiter().GetResult())
            {
                if (!isDisposed() && !cancellationToken.IsCancellationRequested) yield return null;
                yield break;
            }
            reading = null;
            while (!isDisposed() && !cancellationToken.IsCancellationRequested && packets.TryRead(out var packet))
            {
                yield return packet;
            }
        }
    }
}
