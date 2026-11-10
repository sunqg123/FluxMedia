using FFmpeg.AutoGen;
using System.Threading.Channels;

namespace FluxMedia.FFmpeg.Pipeline;

/// <summary>
/// 缓存音视频包并跟踪缓冲时间。
/// </summary>
internal sealed unsafe partial class FFmpegPacketQueue : IDisposable
{
    /// <summary>
    /// 缓存音视频数据包的有界队列。
    /// </summary>
    private readonly Channel<FFmpegPacketLease> _channel;

    /// <summary>
    /// 出队时同步更新统计的数据包读取器。
    /// </summary>
    private readonly ChannelReader<FFmpegPacketLease> _reader;

    /// <summary>
    /// 保护共享状态的同步锁。
    /// </summary>
    private readonly object _sync = new();

    /// <summary>
    /// 将原生时间戳换算为秒的时间基。
    /// </summary>
    private AVRational _timeBase;

    /// <summary>
    /// 缺少时间戳时采用的默认单包时长。
    /// </summary>
    private TimeSpan _fallbackPacketDuration;

    /// <summary>
    /// 媒体原始时间戳的归零起点。
    /// </summary>
    private TimeSpan _timestampOrigin;

    /// <summary>
    /// 是否已释放资源。
    /// </summary>
    private int _disposed;

    /// <summary>
    /// 是否已关闭写入入口；由同步锁保护。
    /// </summary>
    private bool _completed;

    /// <summary>
    /// 压缩视频丢包后需从关键帧恢复，不能任意覆盖参考包。
    /// </summary>
    private readonly bool _requireKeyFrameAfterOverflow;

    /// <summary>
    /// 当前是否正在等待新的完整关键帧。
    /// </summary>
    private bool _waitingForKeyPacket;

    /// <summary>
    /// 已排队数据包数量。
    /// </summary>
    private int _queuedPacketCount;

    /// <summary>
    /// 当前缓冲区首个数据包的媒体时间。
    /// </summary>
    private TimeSpan? _firstTimestamp;

    /// <summary>
    /// 当前缓冲区末个数据包的媒体时间。
    /// </summary>
    private TimeSpan? _lastTimestamp;

    /// <summary>
    /// 当前已处理到的媒体时间。
    /// </summary>
    private TimeSpan _currentTime;

    /// <summary>
    /// 队列当前缓存的媒体时长。
    /// </summary>
    private TimeSpan _bufferedDuration;

    /// <summary>
    /// 初始化 FFmpeg 数据包队列所需的状态和资源。
    /// </summary>
    public FFmpegPacketQueue(
        int capacity, AVRational timeBase = default, TimeSpan? fallbackPacketDuration = null, bool requireKeyFrameAfterOverflow = false)
    {
        _requireKeyFrameAfterOverflow = requireKeyFrameAfterOverflow;
        _timeBase = timeBase.num == 0 || timeBase.den == 0 ? new AVRational { num = 1, den = ffmpeg.AV_TIME_BASE } : timeBase;
        _fallbackPacketDuration = fallbackPacketDuration ?? TimeSpan.FromMilliseconds(33);
        _channel = Channel.CreateBounded<FFmpegPacketLease>(new BoundedChannelOptions(Math.Max(1, capacity))
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = true,
            AllowSynchronousContinuations = false
        });
        _reader = new TrackingPacketReader(this, _channel.Reader);
    }

    /// <summary>
    /// 供解码线程读取缓存数据包的入口。
    /// </summary>
    public ChannelReader<FFmpegPacketLease> Reader => _reader;

    /// <summary>
    /// 因缓冲策略丢弃的数据包总数。
    /// </summary>
    public long DroppedPackets => Interlocked.Read(ref _droppedPackets);

    /// <summary>
    /// 当前处理到的媒体时间。
    /// </summary>
    public TimeSpan CurrentTime
    {
        get
        {
            lock (_sync)
            {
                return _currentTime;
            }
        }
    }

    /// <summary>
    /// 缓冲时长。
    /// </summary>
    public TimeSpan BufferedDuration
    {
        get
        {
            lock (_sync)
            {
                return _bufferedDuration;
            }
        }
    }

    /// <summary>
    /// 当前缓冲区首个包的时间戳。
    /// </summary>
    public TimeSpan? FirstTimestamp
    {
        get
        {
            lock (_sync)
            {
                return _firstTimestamp;
            }
        }
    }

    /// <summary>
    /// 当前缓冲区末个包的时间戳。
    /// </summary>
    public TimeSpan? LastTimestamp
    {
        get
        {
            lock (_sync)
            {
                return _lastTimestamp;
            }
        }
    }

    /// <summary>
    /// 当前缓存的数据包数量。
    /// </summary>
    public int Count => Volatile.Read(ref _queuedPacketCount);

    /// <summary>
    /// 已丢弃数据包。
    /// </summary>
    private long _droppedPackets;

    /// <summary>
    /// 设置数据包时间基、默认时长和时间起点。
    /// </summary>
    public void ConfigureTiming(AVRational timeBase, TimeSpan? fallbackPacketDuration = null, TimeSpan? timestampOrigin = null)
    {
        lock (_sync)
        {
            _timeBase = timeBase.num == 0 || timeBase.den == 0 ? new AVRational { num = 1, den = ffmpeg.AV_TIME_BASE } : timeBase;
            if (fallbackPacketDuration is not null)
            {
                _fallbackPacketDuration = fallbackPacketDuration.Value;
            }
            _timestampOrigin = timestampOrigin ?? TimeSpan.Zero;
        }
    }

    /// <summary>
    /// 队列已满时丢弃旧包，优先保留最新包。
    /// </summary>
    public bool TryWriteLatest(FFmpegPacketLease packet)
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            if (_completed)
            {
                packet.Dispose();
                Interlocked.Increment(ref _droppedPackets);
                return false;
            }
            var keyPacket = (packet.Packet->flags & ffmpeg.AV_PKT_FLAG_KEY) != 0;
            if (_requireKeyFrameAfterOverflow)
            {
                if ((packet.Packet->flags & ffmpeg.AV_PKT_FLAG_CORRUPT) != 0)
                {
                    DropBufferedPackets();
                    _waitingForKeyPacket = true;
                    packet.Dispose();
                    Interlocked.Increment(ref _droppedPackets);
                    return false;
                }
                if (_waitingForKeyPacket && !keyPacket)
                {
                    packet.Dispose();
                    Interlocked.Increment(ref _droppedPackets);
                    return false;
                }
                _waitingForKeyPacket = false;
            }
            if (_channel.Writer.TryWrite(packet))
            {
                OnPacketEnqueued(packet);
                return true;
            }
            if (_requireKeyFrameAfterOverflow)
            {
                DropBufferedPackets();
                _waitingForKeyPacket = !keyPacket;
                if (keyPacket && _channel.Writer.TryWrite(packet))
                {
                    OnPacketEnqueued(packet);
                    return true;
                }
                packet.Dispose();
                Interlocked.Increment(ref _droppedPackets);
                return false;
            }
            if (_channel.Reader.TryRead(out var stale))
            {
                OnPacketDequeued(stale);
                stale.Dispose();
                Interlocked.Increment(ref _droppedPackets);
                if (_channel.Writer.TryWrite(packet))
                {
                    OnPacketEnqueued(packet);
                    return true;
                }
            }
            packet.Dispose();
            Interlocked.Increment(ref _droppedPackets);
            return false;
        }
    }

    /// <summary>
    /// 按顺序写入，满队列时释放新包。
    /// </summary>
    public bool TryWriteOrdered(FFmpegPacketLease packet)
    {
        ThrowIfDisposed();
        if (TryWriteOrderedWithoutDrop(packet)) return true;
        packet.Dispose();
        Interlocked.Increment(ref _droppedPackets);
        return false;
    }

    /// <summary>
    /// 尝试按顺序写入，失败时由调用方处理数据包。
    /// </summary>
    public bool TryWriteOrderedWithoutDrop(FFmpegPacketLease packet)
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            if (!_channel.Writer.TryWrite(packet)) return false;
            OnPacketEnqueued(packet);
            return true;
        }
    }

    /// <summary>
    /// 清空缓存内容并重置相关状态。
    /// </summary>
    public void Clear()
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            _waitingForKeyPacket = false;
            while (_channel.Reader.TryRead(out var packet))
            {
                OnPacketDequeued(packet);
                packet.Dispose();
            }

            ResetTimestamps();
        }
    }

    /// <summary>
    /// 在队列锁内丢弃旧压缩包，结束残缺 GOP。
    /// </summary>
    private void DropBufferedPackets()
    {
        while (_channel.Reader.TryRead(out var packet))
        {
            packet.Dispose();
            Interlocked.Increment(ref _droppedPackets);
        }
        ResetTimestamps();
    }

    /// <summary>
    /// 关闭队列并通知消费者处理结果。
    /// </summary>
    public void Complete(Exception? error = null)
    {
        lock (_sync)
        {
            _completed = true;
            _channel.Writer.TryComplete(error);
        }
    }

    /// <summary>
    /// 释放当前对象持有的资源。
    /// </summary>
    public void Dispose()
    {
        lock (_sync)
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _completed = true;
            _channel.Writer.TryComplete();
            while (_channel.Reader.TryRead(out var packet))
            {
                packet.Dispose();
            }
            ResetTimestamps();
        }
    }

    /// <summary>
    /// 更新入队包数和缓冲时间。
    /// </summary>
    private void OnPacketEnqueued(FFmpegPacketLease packet)
    {
        var timestamp = GetPacketTimestamp(packet);
        lock (_sync)
        {
            if (timestamp is null)
            {
                Interlocked.Increment(ref _queuedPacketCount);
                _bufferedDuration = _bufferedDuration + _fallbackPacketDuration;
                return;
            }

            Interlocked.Increment(ref _queuedPacketCount);
            _lastTimestamp = timestamp.Value;
            _firstTimestamp ??= timestamp.Value;
            _currentTime = _firstTimestamp.Value;
            _bufferedDuration = _lastTimestamp.Value >= _firstTimestamp.Value ? _lastTimestamp.Value - _firstTimestamp.Value : _fallbackPacketDuration * Math.Max(1, Count);
        }
    }

    /// <summary>
    /// 更新出队后的包数和时间统计。
    /// </summary>
    private void OnPacketDequeued(FFmpegPacketLease packet)
    {
        var timestamp = GetPacketTimestamp(packet);
        lock (_sync)
        {
            var count = Math.Max(0, Interlocked.Decrement(ref _queuedPacketCount));
            if (count == 0)
            {
                ResetTimestamps();
                return;
            }

            if (timestamp is not null)
            {
                _currentTime = timestamp.Value;
                _firstTimestamp = timestamp.Value;
            }

            if (_firstTimestamp is not null && _lastTimestamp is not null)
            {
                _bufferedDuration = _lastTimestamp.Value >= _firstTimestamp.Value
                    ? _lastTimestamp.Value - _firstTimestamp.Value
                    : _fallbackPacketDuration * count;
            }
            else
            {
                _bufferedDuration = _fallbackPacketDuration * count;
            }
        }
    }

    /// <summary>
    /// 重置包计数和缓冲时间。
    /// </summary>
    private void ResetTimestamps()
    {
        _firstTimestamp = null;
        _lastTimestamp = null;
        _currentTime = TimeSpan.Zero;
        _bufferedDuration = TimeSpan.Zero;
        Interlocked.Exchange(ref _queuedPacketCount, 0);
    }

    /// <summary>
    /// 将包时间戳换算为相对媒体时间。
    /// </summary>
    private TimeSpan? GetPacketTimestamp(FFmpegPacketLease packet)
    {
        var raw = packet.Packet->dts != ffmpeg.AV_NOPTS_VALUE ? packet.Packet->dts : packet.Packet->pts;
        if (raw == ffmpeg.AV_NOPTS_VALUE) return null;
        var seconds = raw * ffmpeg.av_q2d(_timeBase);
        if (double.IsNaN(seconds) || double.IsInfinity(seconds)) return null;
        var timestamp = TimeSpan.FromSeconds(seconds) - _timestampOrigin;
        return timestamp < TimeSpan.Zero ? TimeSpan.Zero : timestamp;
    }

    /// <summary>
    /// 对象已释放时阻止继续操作。
    /// </summary>
    private void ThrowIfDisposed()
    {
        if (_disposed != 0)
        {
            throw new ObjectDisposedException(nameof(FFmpegPacketQueue));
        }
    }
}
