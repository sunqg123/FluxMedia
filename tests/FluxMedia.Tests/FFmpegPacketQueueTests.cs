using FFmpeg.AutoGen;
using FluxMedia.FFmpeg.Pipeline;
using FluxMedia.Playback;

namespace FluxMedia.Tests;

/// <summary>
/// FFmpeg 数据包队列测试。
/// </summary>
public sealed unsafe class FFmpegPacketQueueTests
{
    /// <summary>
    /// 压缩视频队列溢出后，丢弃依赖已丢失参考帧的包并等待关键帧。
    /// </summary>
    [FFmpegFact]
    public void VideoOverflow_ResumesOnlyAtNextKeyPacket()
    {
        Assert.True(TryInitializeFFmpeg());
        using var queue = new FFmpegPacketQueue(3, requireKeyFrameAfterOverflow: true);
        Assert.True(queue.TryWriteLatest(CreatePacket(0, key: true)));
        Assert.True(queue.Reader.TryRead(out var first));
        first.Dispose();
        queue.TryWriteLatest(CreatePacket(1));
        queue.TryWriteLatest(CreatePacket(2));
        queue.TryWriteLatest(CreatePacket(3));
        Assert.False(queue.TryWriteLatest(CreatePacket(4)));
        Assert.False(queue.TryWriteLatest(CreatePacket(5)));
        Assert.False(queue.Reader.TryRead(out _));
        Assert.True(queue.TryWriteLatest(CreatePacket(6, key: true)));
        Assert.True(queue.Reader.TryRead(out var recovered));
        using (recovered) Assert.Equal(6, recovered.Packet->pts);
        Assert.Equal(0, queue.Count);
        Assert.Equal(5, queue.DroppedPackets);
    }

    /// <summary>
    /// 验证包队列正确统计缓冲时长和当前位置。
    /// </summary>
    [FFmpegFact]
    public void PacketQueue_TracksBufferedTimeAndCurrentTime()
    {
        FluxEngine.Shutdown();
        if (!TryInitializeFFmpeg())
        {
            return;
        }

        using var queue = new FFmpegPacketQueue(8, new AVRational { num = 1, den = 1000 }, TimeSpan.FromMilliseconds(40));
        Assert.True(queue.TryWriteOrdered(CreatePacket(1000, key: true)));
        Assert.True(queue.TryWriteOrdered(CreatePacket(2000)));
        Assert.True(queue.TryWriteOrdered(CreatePacket(3000)));

        Assert.Equal(3, queue.Count);
        Assert.Equal(TimeSpan.FromSeconds(1), queue.CurrentTime);
        Assert.Equal(TimeSpan.FromSeconds(2), queue.BufferedDuration);

        Assert.True(queue.Reader.TryRead(out var first));
        first.Dispose();

        Assert.Equal(2, queue.Count);
        Assert.Equal(TimeSpan.FromSeconds(1), queue.CurrentTime);
        Assert.Equal(TimeSpan.FromSeconds(2), queue.BufferedDuration);

        Assert.True(queue.Reader.TryRead(out var second));
        second.Dispose();

        Assert.Equal(1, queue.Count);
        Assert.Equal(TimeSpan.FromSeconds(2), queue.CurrentTime);
        Assert.Equal(TimeSpan.FromSeconds(1), queue.BufferedDuration);
    }

    /// <summary>
    /// 本地依赖存在时初始化 FFmpeg 测试环境。
    /// </summary>
    private static bool TryInitializeFFmpeg()
    {
        var ffmpegRoot = FFmpegFactAttribute.NativeRoot;
        if (!FFmpegLibraries.IsAvailable(ffmpegRoot))
        {
            return false;
        }

        FluxEngine.Shutdown();
        FluxEngine.Start(new FluxEngineOptions { FFmpegPath = ffmpegRoot });
        return true;
    }

    /// <summary>
    /// 并发消费并立即释放数据包后计数必须归零。
    /// </summary>
    [FFmpegFact]
    public void ConcurrentPublishAndDispose_LeavesNoPhantomPackets()
    {
        Assert.True(TryInitializeFFmpeg());
        using var queue = new FFmpegPacketQueue(8);
        var producer = Task.Run(() =>
        {
            try
            {
                for (var index = 0; index < 20000; index++)
                    queue.TryWriteLatest(CreatePacket(index));
            }
            finally { queue.Complete(); }
        });
        var consumer = Task.Run(() =>
        {
            while (queue.Reader.WaitToReadAsync().AsTask().GetAwaiter().GetResult())
                while (queue.Reader.TryRead(out var packet)) packet.Dispose();
        });

        Assert.True(Task.WaitAll(new[] { producer, consumer }, TimeSpan.FromSeconds(15)));
        Assert.Equal(0, queue.Count);
        Assert.Equal(TimeSpan.Zero, queue.BufferedDuration);
    }

    /// <summary>
    /// 创建带指定时间戳和关键帧标记的测试包。
    /// </summary>
    private static FFmpegPacketLease CreatePacket(long timestampMs, bool key = false)
    {
        var packet = ffmpeg.av_packet_alloc();
        if (packet == null)
        {
            throw new InvalidOperationException("Failed to allocate test packet.");
        }

        packet->stream_index = 0;
        packet->pts = timestampMs;
        packet->dts = timestampMs;
        packet->duration = 1;
        packet->flags = key ? ffmpeg.AV_PKT_FLAG_KEY : 0;
        return new FFmpegPacketLease(packet, 0);
    }
}
