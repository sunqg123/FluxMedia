using System.Threading.Channels;

namespace FluxMedia.FFmpeg.Pipeline;

// 读取数据包并维护队列统计。
internal sealed unsafe partial class FFmpegPacketQueue
{
    /// <summary>
    /// 取出数据包时同步更新缓冲统计。
    /// </summary>
    private sealed class TrackingPacketReader : ChannelReader<FFmpegPacketLease>
    {
        /// <summary>
        /// 负责维护包数量和时间统计的队列。
        /// </summary>
        private readonly FFmpegPacketQueue _owner;

        /// <summary>
        /// 底层数据包队列的读取器。
        /// </summary>
        private readonly ChannelReader<FFmpegPacketLease> _inner;

        /// <summary>
        /// 初始化统计数据包读取器所需的状态和资源。
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
        /// 尝试取出一个数据包并更新统计。
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
