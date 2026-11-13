using FluxMedia.Core.Models;
using System.Threading.Channels;

namespace FluxMedia.Playback.Internal;

/// <summary>
/// 分发公开帧租约，不管理解码线程。
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
    /// 发布帧租约；原始帧仍由帧泵负责释放。
    /// </summary>
    public async ValueTask PublishAsync(VideoFrame frame, Func<VideoFrame, ValueTask> publish)
    {
        lock (_sync)
        {
            if (!frame.IsCurrent)
            {
                return;
            }
            if (_output is not null)
            {
                while (_output.Reader.TryRead(out var previous)) previous.Dispose();
                var lease = frame.Retain();
                if (!_output.Writer.TryWrite(lease)) lease.Dispose();
            }
        }
        // 视频链路③触发：回到会话 PublishVideoFrameAsync。
        if (frame.IsCurrent) await publish(frame).ConfigureAwait(false);
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
