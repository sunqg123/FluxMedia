using System.Threading.Channels;

namespace FluxMedia.FFmpeg.Pipeline;

// 读取数据包并维护缓冲水位。
internal sealed unsafe partial class FFmpegPacketQueue
{
    /// <summary>
    /// 取出数据包时同步更新缓冲水位。
    /// </summary>
    private sealed class TrackingPacketReader : ChannelReader<FFmpegPacketLease>
    {
        /// <summary>
        /// 维护包数和缓冲时序的队列。
        /// </summary>
        private readonly FFmpegPacketQueue _owner;

        /// <summary>
        /// 底层数据包队列的读取器。
        /// </summary>
        private readonly ChannelReader<FFmpegPacketLease> _inner;

        /// <summary>
        /// 绑定缓冲队列和底层读取器。
        /// </summary>
        public TrackingPacketReader(FFmpegPacketQueue owner, ChannelReader<FFmpegPacketLease> inner)
        {
            _owner = owner;
            _inner = inner;
        }

        /// <summary>
        /// 队列已关闭且数据消费完毕的完成任务。
        /// </summary>
        public override Task Completion => _inner.Completion;

        /// <summary>
        /// 是否支持计数。
        /// </summary>
        public override bool CanCount => true;

        /// <summary>
        /// 数量。
        /// </summary>
        public override int Count => _owner.Count;

        /// <summary>
        /// 尝试取出数据包并更新缓冲水位。
        /// </summary>
        public override bool TryRead(out FFmpegPacketLease item)
        {
            lock (_owner._sync)
            {
                if (_inner.TryRead(out item!))
                {
                    _owner.OnPacketDequeued(item);
                    return true;
                }

                return false;
            }
        }

        /// <summary>
        /// 等待队列出现可读取的数据。
        /// </summary>
        public override ValueTask<bool> WaitToReadAsync(CancellationToken cancellationToken = default)
        {
            return _inner.WaitToReadAsync(cancellationToken);
        }
    }
}
