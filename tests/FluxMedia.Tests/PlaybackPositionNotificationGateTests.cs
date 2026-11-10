using FluxMedia.Playback.Internal;

namespace FluxMedia.Tests;

/// <summary>
/// 播放位置通知同步锁测试。
/// </summary>
public sealed class PlaybackPositionNotificationGateTests
{
    /// <summary>
    /// 验证进度首次立即通知，随后按间隔限流。
    /// </summary>
    [Fact]
    public void Gate_PublishesImmediatelyThenThrottlesUntilIntervalElapsed()
    {
        var gate = new PlaybackPositionNotificationGate(TimeSpan.FromMilliseconds(100));
        var start = DateTimeOffset.UtcNow;

        Assert.True(gate.ShouldPublish(start));
        Assert.False(gate.ShouldPublish(start.AddMilliseconds(16)));
        Assert.False(gate.ShouldPublish(start.AddMilliseconds(99)));
        Assert.True(gate.ShouldPublish(start.AddMilliseconds(100)));
    }

    /// <summary>
    /// 验证重置后下一次进度立即通知。
    /// </summary>
    [Fact]
    public void Reset_AllowsNextPositionToPublishImmediately()
    {
        var gate = new PlaybackPositionNotificationGate(TimeSpan.FromMilliseconds(100));
        var start = DateTimeOffset.UtcNow;

        Assert.True(gate.ShouldPublish(start));
        gate.Reset();

        Assert.True(gate.ShouldPublish(start.AddMilliseconds(1)));
    }
}
