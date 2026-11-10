using FluxMedia.Core.Models;
using FluxMedia.Playback.Internal;

namespace FluxMedia.Tests;

/// <summary>
/// 验证每代帧队列的独立所有权。
/// </summary>
public sealed class PlaybackFramePumpTests
{
    /// <summary>
    /// 正常结束仅发布最后一个待显示帧。
    /// </summary>
    [Fact]
    public async Task Pump_PublishesLatestFrame()
    {
        var hub = new PlaybackVideoFrameHub();
        var sequences = new List<long>();
        using var pump = new PlaybackFramePump(hub, (frame, _) => { sequences.Add(frame.Sequence); return default; });
        pump.Queue(CreateFrame(1));
        pump.Queue(CreateFrame(2));
        pump.Queue(CreateFrame(3));
        pump.Complete();
        await pump.RunAsync(CancellationToken.None);
        Assert.Equal(new[] { 3L }, sequences);
        Assert.Equal(2, hub.DroppedFrameCount);
        Assert.Equal(1, hub.VideoFrameReadyCount);
    }

    /// <summary>
    /// 停止时归还未发布帧，重复停止不重复持有资源。
    /// </summary>
    [Fact]
    public void Dispose_ReleasesPendingFrame()
    {
        var hub = new PlaybackVideoFrameHub();
        using var pump = new PlaybackFramePump(hub, (_, _) => default);
        var frame = CreateFrame(1);
        pump.Queue(frame);
        pump.Dispose();
        pump.Dispose();
        Assert.Throws<ObjectDisposedException>(() => frame.Retain());
    }

    /// <summary>
    /// 旧代迟到的帧不会进入新代队列。
    /// </summary>
    [Fact]
    public async Task RetiredPump_CannotPublishIntoNextGeneration()
    {
        var hub = new PlaybackVideoFrameHub();
        var published = new List<long>();
        using var old = new PlaybackFramePump(hub, (frame, _) => { published.Add(frame.Sequence); return default; });
        old.Dispose();
        using var next = new PlaybackFramePump(hub, (frame, _) => { published.Add(frame.Sequence); return default; });
        var late = CreateFrame(1);
        old.Queue(late);
        next.Queue(CreateFrame(2));
        next.Complete();
        await next.RunAsync(CancellationToken.None);
        Assert.Throws<ObjectDisposedException>(() => late.Retain());
        Assert.Equal(new[] { 2L }, published);
    }

    /// <summary>
    /// 已取消的帧泵不发布排队中的帧。
    /// </summary>
    [Fact]
    public async Task CanceledPump_ReleasesPendingFrame()
    {
        var hub = new PlaybackVideoFrameHub();
        using var pump = new PlaybackFramePump(hub, (_, _) => throw new InvalidOperationException("不应发布。"));
        var frame = CreateFrame(1);
        pump.Queue(frame);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pump.RunAsync(new CancellationToken(true)));
        Assert.Throws<ObjectDisposedException>(() => frame.Retain());
    }

    /// <summary>
    /// 回调失败后释放当前帧和剩余帧。
    /// </summary>
    [Fact]
    public async Task PublishFailure_ReleasesOwnedFrames()
    {
        var hub = new PlaybackVideoFrameHub();
        var first = CreateFrame(1);
        var next = CreateFrame(2);
        PlaybackFramePump? pump = null;
        using (pump = new PlaybackFramePump(hub, (_, _) =>
        {
            pump!.Queue(next);
            throw new InvalidOperationException("模拟发布失败。");
        }))
        {
            pump.Queue(first);
            await Assert.ThrowsAsync<InvalidOperationException>(() => pump.RunAsync(CancellationToken.None));
        }
        Assert.Throws<ObjectDisposedException>(() => first.Retain());
        Assert.Throws<ObjectDisposedException>(() => next.Retain());
    }

    /// <summary>
    /// 关闭后的队列立即释放新提交的帧。
    /// </summary>
    [Fact]
    public void ClosedPump_ReleasesLateFrame()
    {
        var hub = new PlaybackVideoFrameHub();
        using var pump = new PlaybackFramePump(hub, (_, _) => default);
        pump.Dispose();
        var frame = CreateFrame(1);
        pump.Queue(frame);
        Assert.Throws<ObjectDisposedException>(() => frame.Retain());
    }

    /// <summary>
    /// 创建可检查生命周期的最小视频帧。
    /// </summary>
    private static VideoFrame CreateFrame(long sequence) => new(
        new VideoFormat(1, 1, 25, PixelFormat.Bgra32), new byte[] { 0, 0, 0, 255 },
        TimeSpan.FromMilliseconds(sequence * 40), sequence, 4);
}
