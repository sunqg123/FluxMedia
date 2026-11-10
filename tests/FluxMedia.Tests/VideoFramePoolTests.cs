using FluxMedia.Core.Models;
using FluxMedia.Playback.Internal;

namespace FluxMedia.Tests;

/// <summary>
/// 视频帧池测试。
/// </summary>
public sealed class VideoFramePoolTests
{
    /// <summary>
    /// 验证持有租约时帧内存仍可读取。
    /// </summary>
    [Fact]
    public void PooledVideoFrame_RemainsReadableWhileLeaseIsAlive()
    {
        var owner = VideoFramePool.Rent(4);
        owner.Memory.Span.Fill(42);
        var frame = new VideoFrame(
            new VideoFormat(1, 1, 25, PixelFormat.Bgra32), owner, 4, TimeSpan.Zero, sequence: 1, stride: 4);

        using var lease = frame.Retain();
        frame.Dispose();

        Assert.Equal(42, lease.Frame.Buffer.Span[0]);
    }

    /// <summary>
    /// 验证完全释放的帧不能再次保留引用。
    /// </summary>
    [Fact]
    public void PooledVideoFrame_RejectsRetainAfterDispose()
    {
        var owner = VideoFramePool.Rent(4);
        var frame = new VideoFrame(
            new VideoFormat(1, 1, 25, PixelFormat.Bgra32), owner, 4, TimeSpan.Zero, sequence: 1, stride: 4);

        frame.Dispose();

        Assert.Throws<ObjectDisposedException>(() => frame.Retain());
    }

    /// <summary>
    /// 验证已有租约允许继续保留帧引用。
    /// </summary>
    [Fact]
    public void PooledVideoFrame_CanRetainAgainThroughAnExistingLease()
    {
        var owner = VideoFramePool.Rent(4);
        owner.Memory.Span.Fill(23);
        var frame = new VideoFrame(
            new VideoFormat(1, 1, 25, PixelFormat.Bgra32), owner, 4, TimeSpan.Zero, sequence: 1, stride: 4);

        using var publicLease = frame.Retain();
        frame.Dispose();
        using var retainedByConsumer = publicLease.Frame.Retain();

        Assert.Equal(23, retainedByConsumer.Frame.Buffer.Span[0]);
    }
}
