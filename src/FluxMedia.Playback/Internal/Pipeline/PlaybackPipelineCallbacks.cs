using FluxMedia.Core.Models;

namespace FluxMedia.Playback.Internal;

/// <summary>
/// 一次管线读取的状态和通知回调。
/// </summary>
/// <param name="IsPaused">读取暂停状态。</param>
/// <param name="PlaybackRate">读取播放速度。</param>
/// <param name="Volume">读取音量。</param>
/// <param name="IsMuted">读取静音状态。</param>
/// <param name="Resync">读取解码同步请求。</param>
/// <param name="VideoCapabilities">读取显示端能力。</param>
/// <param name="VideoDiagnostics">上报视频状态。</param>
/// <param name="AudioDiagnostics">上报音频状态。</param>
/// <param name="PublishFrame">发布当前管线的帧。</param>
internal sealed record PlaybackPipelineCallbacks(
    Func<bool> IsPaused,
    Func<double> PlaybackRate,
    Func<double> Volume,
    Func<bool> IsMuted,
    Func<PlaybackResyncRequest> Resync,
    Func<VideoOutputCapabilities> VideoCapabilities,
    Action<PlaybackWorkerDiagnostics> VideoDiagnostics,
    Action<AudioPlaybackDiagnostics> AudioDiagnostics,
    Func<VideoFrame, long, ValueTask> PublishFrame);
