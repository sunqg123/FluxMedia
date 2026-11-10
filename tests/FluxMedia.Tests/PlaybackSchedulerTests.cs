using FluxMedia.Playback;
using FluxMedia.Playback.Internal;

namespace FluxMedia.Tests;

/// <summary>
/// 播放调度器测试。
/// </summary>
public sealed class PlaybackSchedulerTests
{
    /// <summary>
    /// 验证无音频时仍能按视频时间显示帧。
    /// </summary>
    [Fact]
    public void ScheduleVideoFrame_WithoutAudio_PresentsFrame()
    {
        var scheduler = new PlaybackScheduler(
            PlaybackTimingMode.RealtimeLowLatency, () => 1, () => false);

        var decision = scheduler.ScheduleVideoFrame(TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(40), CancellationToken.None);

        Assert.Equal(VideoFrameSchedulingAction.Present, decision.Action);
        Assert.Null(decision.AudioClock);
        Assert.Null(decision.DistanceToAudio);
    }

    /// <summary>
    /// 验证视频明显落后于音频时丢弃当前帧。
    /// </summary>
    [Fact]
    public void ScheduleVideoFrame_WhenVideoIsTooLate_DropsFrame()
    {
        var audio = new AudioPlaybackDiagnostics
        {
            WorkerState = "Running",
            CurrentTime = TimeSpan.FromSeconds(10),
            PresentationTime = TimeSpan.FromSeconds(9.9),
            OutputBufferedDuration = TimeSpan.FromMilliseconds(100),
            OutputDeviceDelay = TimeSpan.Zero
        };
        var scheduler = new PlaybackScheduler(
            PlaybackTimingMode.PresentationTimestamp, () => 1, () => false, () => audio);

        var decision = scheduler.ScheduleVideoFrame(TimeSpan.FromSeconds(9), TimeSpan.FromMilliseconds(40), CancellationToken.None);

        Assert.Equal(VideoFrameSchedulingAction.Drop, decision.Action);
        Assert.Equal(TimeSpan.FromSeconds(9.9), decision.AudioClock);
        Assert.True(decision.DistanceToAudio < TimeSpan.Zero);
    }

    /// <summary>
    /// 验证视频领先音频时等待后再显示。
    /// </summary>
    [Fact]
    public void ScheduleVideoFrame_WhenVideoIsAheadOfAudio_PresentsAfterSyncWait()
    {
        var audio = new AudioPlaybackDiagnostics
        {
            WorkerState = "Running",
            CurrentTime = TimeSpan.FromSeconds(1),
            PresentationTime = TimeSpan.FromSeconds(0.9),
            OutputBufferedDuration = TimeSpan.FromMilliseconds(100),
            OutputDeviceDelay = TimeSpan.Zero
        };
        var scheduler = new PlaybackScheduler(
            PlaybackTimingMode.PresentationTimestamp, () => 1, () => false, () => audio);

        var decision = scheduler.ScheduleVideoFrame(TimeSpan.FromMilliseconds(1030), TimeSpan.FromMilliseconds(40), CancellationToken.None);

        Assert.Equal(VideoFrameSchedulingAction.Present, decision.Action);
        Assert.Equal(TimeSpan.FromMilliseconds(900), decision.AudioClock);
        Assert.True(decision.DistanceToAudio > TimeSpan.Zero);
    }

    /// <summary>
    /// 验证诊断更新间隔内音频时钟仍继续推进。
    /// </summary>
    [Fact]
    public void ScheduleVideoFrame_AdvancesAudioClockBetweenDiagnosticUpdates()
    {
        var audio = new AudioPlaybackDiagnostics
        {
            WorkerState = "Running",
            CurrentTime = TimeSpan.FromSeconds(1),
            PresentationTime = TimeSpan.FromSeconds(0.9),
            OutputBufferedDuration = TimeSpan.FromMilliseconds(100),
            OutputDeviceDelay = TimeSpan.Zero,
            UpdatedAt = DateTimeOffset.UtcNow - TimeSpan.FromMilliseconds(200)
        };
        var scheduler = new PlaybackScheduler(
            PlaybackTimingMode.PresentationTimestamp, () => 1, () => false, () => audio);

        var decision = scheduler.ScheduleVideoFrame(
            TimeSpan.FromMilliseconds(1200), TimeSpan.FromMilliseconds(40), CancellationToken.None);

        Assert.Equal(VideoFrameSchedulingAction.Present, decision.Action);
        Assert.InRange(
            decision.AudioClock!.Value, TimeSpan.FromMilliseconds(1050), TimeSpan.FromMilliseconds(1200));
        Assert.InRange(
            decision.Waited, TimeSpan.Zero, TimeSpan.FromMilliseconds(150));
    }

    /// <summary>
    /// 验证不再使用已退出音频线程的时钟。
    /// </summary>
    [Fact]
    public void ScheduleVideoFrame_DoesNotSyncToExitedAudioWorker()
    {
        var audio = new AudioPlaybackDiagnostics
        {
            WorkerState = "Exited",
            CurrentTime = TimeSpan.FromSeconds(1),
            PresentationTime = TimeSpan.FromSeconds(0.9),
            UpdatedAt = DateTimeOffset.UtcNow
        };
        var scheduler = new PlaybackScheduler(
            PlaybackTimingMode.PresentationTimestamp, () => 1, () => false, () => audio);

        var decision = scheduler.ScheduleVideoFrame(
            TimeSpan.Zero, TimeSpan.FromMilliseconds(40), CancellationToken.None);

        Assert.Equal(VideoFrameSchedulingAction.Present, decision.Action);
        Assert.Null(decision.AudioClock);
    }

    /// <summary>
    /// 验证音频缓冲时钟不可靠时使用视频调度。
    /// </summary>
    [Fact]
    public void ScheduleVideoFrame_DoesNotSyncToAudioWithoutReliableBufferClock()
    {
        var audio = new AudioPlaybackDiagnostics
        {
            WorkerState = "Running",
            CurrentTime = TimeSpan.FromSeconds(5),
            PresentationTime = TimeSpan.FromSeconds(5),
            OutputBufferedDuration = TimeSpan.Zero,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        var scheduler = new PlaybackScheduler(
            PlaybackTimingMode.PresentationTimestamp, () => 1, () => false, () => audio);

        var decision = scheduler.ScheduleVideoFrame(
            TimeSpan.Zero, TimeSpan.FromMilliseconds(40), CancellationToken.None);

        Assert.Equal(VideoFrameSchedulingAction.Present, decision.Action);
        Assert.Null(decision.AudioClock);
    }

    /// <summary>
    /// 验证首次缓冲不足时继续等待数据。
    /// </summary>
    [Fact]
    public void ScheduleVideoFrame_WhenInitialBufferIsTooLow_ReturnsBuffer()
    {
        var scheduler = new PlaybackScheduler(
            PlaybackTimingMode.PresentationTimestamp, () => 1, () => false, videoBufferedDuration: () => TimeSpan.FromMilliseconds(100), videoBufferedPacketCount: () => 2, minBufferDuration: TimeSpan.FromMilliseconds(500));

        var decision = scheduler.ScheduleVideoFrame(TimeSpan.Zero, TimeSpan.FromMilliseconds(40), CancellationToken.None);

        Assert.Equal(VideoFrameSchedulingAction.Buffer, decision.Action);
        Assert.Equal(TimeSpan.FromMilliseconds(100), decision.VideoBufferedDuration);
    }

    /// <summary>
    /// 验证低延迟模式在积压过高时主动丢帧。
    /// </summary>
    [Fact]
    public void ScheduleVideoFrame_InLowLatencyMode_WhenBufferedDelayIsHigh_DropsFrame()
    {
        var scheduler = new PlaybackScheduler(
            PlaybackTimingMode.RealtimeLowLatency, () => 1, () => false, videoBufferedDuration: () => TimeSpan.FromMilliseconds(200), videoBufferedPacketCount: () => 3, maxLowLatencyDelay: TimeSpan.FromMilliseconds(60));

        var decision = scheduler.ScheduleVideoFrame(TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(40), CancellationToken.None);

        Assert.Equal(VideoFrameSchedulingAction.Drop, decision.Action);
        Assert.True(decision.LowLatencyCatchUp);
        Assert.Equal(TimeSpan.FromMilliseconds(200), decision.VideoBufferedDuration);
    }
}
