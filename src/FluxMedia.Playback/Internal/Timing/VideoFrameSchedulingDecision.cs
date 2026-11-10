namespace FluxMedia.Playback.Internal;

/// <summary>
/// 视频帧的调度结果和等待时间。
/// </summary>
/// <param name="Action">当前视频帧应执行的调度动作。</param>
/// <param name="Waited">调度时实际等待的时长。</param>
/// <param name="DistanceToAudio">当前视频帧相对音频时钟的时间差。</param>
/// <param name="AudioClock">估算的当前音频播放时间。</param>
/// <param name="VideoBufferedDuration">视频队列中剩余的缓冲时长。</param>
/// <param name="LowLatencyCatchUp">是否正在通过丢帧追赶实时进度。</param>
internal readonly record struct VideoFrameSchedulingDecision(VideoFrameSchedulingAction Action, TimeSpan Waited, TimeSpan? DistanceToAudio, TimeSpan? AudioClock, TimeSpan VideoBufferedDuration, bool LowLatencyCatchUp);
