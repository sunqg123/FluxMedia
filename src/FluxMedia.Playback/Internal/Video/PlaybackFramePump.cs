using FluxMedia.Core.Models;
using System.Threading.Channels;

namespace FluxMedia.Playback.Internal;

/// <summary>
/// 一次管线运行独占的最新帧队列。
/// </summary>
internal sealed class PlaybackFramePump : IDisposable
{
    /// <summary>
    /// 待发布帧，旧管线永远不能写入新管线的队列。
    /// </summary>
    private readonly Channel<VideoFrame> _input = Channel.CreateBounded<VideoFrame>(new BoundedChannelOptions(1)
    {
        FullMode = BoundedChannelFullMode.Wait,
        SingleReader = false,
        SingleWriter = false
    });
    /// <summary>
    /// 跨管线累计的公开输出与统计。
    /// </summary>
    private readonly PlaybackVideoFrameHub _output;
    /// <summary>
    /// 绑定本次管线的帧回调。
    /// </summary>
    private readonly Func<VideoFrame, long, ValueTask> _publish;

    /// <summary>
    /// 绑定输出端与帧回调。
    /// </summary>
    public PlaybackFramePump(PlaybackVideoFrameHub output, Func<VideoFrame, long, ValueTask> publish)
    {
        _output = output;
        _publish = publish;
    }
    /// <summary>
    /// 接管原始帧，覆盖旧帧时立即释放。
    /// </summary>
    public void Queue(VideoFrame frame)
    {
        if (!frame.IsCurrent) { frame.Dispose(); _output.RecordDrop(); return; }
        while (_input.Reader.TryRead(out var previous))
        {
            previous.Dispose();
            _output.RecordDrop();
        }
        if (!_input.Writer.TryWrite(frame))
        {
            frame.Dispose();
            _output.RecordDrop();
        }
    }
    /// <summary>
    /// 正常结束时允许帧泵消费最后一帧。
    /// </summary>
    public void Complete() => _input.Writer.TryComplete();
    /// <summary>
    /// 持续发布最新帧，任何退出路径均归还残留帧。
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var frame in _input.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!frame.IsCurrent) { _output.RecordDrop(); continue; }
                    await _output.PublishAsync(frame, _publish).ConfigureAwait(false);
                }
                finally { frame.Dispose(); }
            }
        }
        finally { Dispose(); }
    }
    /// <summary>
    /// 关闭入口并归还所有未发布帧，可重复调用。
    /// </summary>
    public void Dispose()
    {
        _input.Writer.TryComplete();
        while (_input.Reader.TryRead(out var frame))
        {
            frame.Dispose();
            _output.RecordDrop();
        }
    }
}
