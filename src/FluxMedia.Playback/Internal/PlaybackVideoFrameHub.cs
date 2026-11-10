using System.Threading.Channels;
using FluxMedia.Core.Models;

namespace FluxMedia.Playback.Internal;

/// <summary>
/// 分发公开帧租约并累计显示统计，不管理解码线程。
/// </summary>
internal sealed class PlaybackVideoFrameHub
{
    /// <summary>
    /// 保护公开输出通道的切换。
    /// </summary>
    private readonly object _sync = new();
    /// <summary>
    /// 当前公开帧输出通道。
    /// </summary>
    private Channel<VideoFrameLease>? _output;
    /// <summary>
    /// 累计发布的帧数。
    /// </summary>
    private long _outputFrameCount;
    /// <summary>
    /// 管线覆盖或清理的帧数。
    /// </summary>
    private long _droppedFrameCount;
    /// <summary>
    /// 累计发布帧数。
    /// </summary>
    public long OutputFrameCount => Interlocked.Read(ref _outputFrameCount);
    /// <summary>
    /// 累计丢弃帧数。
    /// </summary>
    public long DroppedFrameCount => Interlocked.Read(ref _droppedFrameCount);
    /// <summary>
    /// 帧就绪通知数。
    /// </summary>
    public long VideoFrameReadyCount => OutputFrameCount;
    /// <summary>
    /// 开始打开新媒体时重置统计。
    /// </summary>
    public void ResetCounters()
    {
        Interlocked.Exchange(ref _outputFrameCount, 0);
        Interlocked.Exchange(ref _droppedFrameCount, 0);
    }
    /// <summary>
    /// 记录管线丢弃的帧。
    /// </summary>
    public void RecordDrop() => Interlocked.Increment(ref _droppedFrameCount);
    /// <summary>
    /// 发布帧租约；原始帧仍由帧泵负责释放。
    /// </summary>
    public async ValueTask PublishAsync(VideoFrame frame, Func<VideoFrame, long, ValueTask> publish)
    {
        long count;
        lock (_sync)
        {
            if (!frame.IsCurrent)
            {
                RecordDrop();
                return;
            }
            count = Interlocked.Increment(ref _outputFrameCount);
            if (_output is not null)
            {
                while (_output.Reader.TryRead(out var previous)) previous.Dispose();
                var lease = frame.Retain();
                if (!_output.Writer.TryWrite(lease)) lease.Dispose();
            }
        }
        if (frame.IsCurrent) await publish(frame, count).ConfigureAwait(false);
        else RecordDrop();
    }
    /// <summary>
    /// 释放公开队列中尚未消费的帧。
    /// </summary>
    public void ClearPublicOutput()
    {
        lock (_sync)
            if (_output is not null)
                while (_output.Reader.TryRead(out var lease)) lease.Dispose();
    }
    /// <summary>
    /// 关闭旧输出，下次读取创建新的通道。
    /// </summary>
    public void ResetPublicOutput()
    {
        lock (_sync)
        {
            _output?.Writer.TryComplete();
            ClearPublicOutput();
            _output = null;
        }
    }
    /// <summary>
    /// 顺序读取最新帧，跨迭代使用需保留租约。
    /// </summary>
    public async IAsyncEnumerable<VideoFrame> ReadVideoFramesAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Channel<VideoFrameLease> output;
        lock (_sync)
            output = _output ??= Channel.CreateBounded<VideoFrameLease>(new BoundedChannelOptions(1)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = false,
                SingleWriter = false
            });
        await foreach (var lease in output.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            try
            {
                if (lease.Frame.IsCurrent)
                {
                    yield return lease.Frame;
                }
            }
            finally
            {
                lease.Dispose();
            }
        }
    }
}
