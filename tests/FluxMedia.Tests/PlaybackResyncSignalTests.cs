using FluxMedia.Playback.Internal;

namespace FluxMedia.Tests;

/// <summary>
/// 播放重新同步信号测试。
/// </summary>
public class PlaybackResyncSignalTests
{
    /// <summary>
    /// 验证同步请求递增版本并保存目标位置。
    /// </summary>
    [Fact]
    public void Request_IncrementsVersionAndPublishesPosition()
    {
        var signal = new PlaybackResyncSignal();

        var first = signal.Request(TimeSpan.FromSeconds(12));
        var second = signal.Request(TimeSpan.FromSeconds(30));
        var snapshot = signal.Snapshot();

        Assert.Equal(1, first.Version);
        Assert.Equal(TimeSpan.FromSeconds(12), first.Position);
        Assert.Equal(2, second.Version);
        Assert.Equal(TimeSpan.FromSeconds(30), second.Position);
        Assert.Equal(second, snapshot);
    }
}
