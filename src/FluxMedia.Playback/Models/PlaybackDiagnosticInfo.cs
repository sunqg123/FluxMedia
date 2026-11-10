using FluxMedia.Core.Models;

namespace FluxMedia.Playback;

/// <summary>
/// 播放诊断信息。
/// </summary>
public sealed record PlaybackDiagnosticInfo
{
    /// <summary>
    /// 源类型。
    /// </summary>
    public MediaSourceKind? SourceKind { get; init; }

    /// <summary>
    /// 源地址。
    /// </summary>
    public string? SourceUrl { get; init; }

    /// <summary>
    /// FFmpeg 打开配置。
    /// </summary>
    public string? FFmpegOpenOptions { get; init; }

    /// <summary>
    /// 打开耗时。
    /// </summary>
    public TimeSpan? OpenElapsed { get; init; }

    /// <summary>
    /// 流信息耗时。
    /// </summary>
    public TimeSpan? StreamInfoElapsed { get; init; }

    /// <summary>
    /// 首次帧耗时。
    /// </summary>
    public TimeSpan? FirstFrameElapsed { get; init; }

    /// <summary>
    /// 首次已解码帧耗时。
    /// </summary>
    public TimeSpan? FirstDecodedFrameElapsed { get; init; }

    /// <summary>
    /// 界面首帧耗时。
    /// </summary>
    public TimeSpan? FirstUiFrameElapsed { get; init; }

    /// <summary>
    /// 协议。
    /// </summary>
    public string? Protocol { get; init; }

    /// <summary>
    /// 容器。
    /// </summary>
    public string? Container { get; init; }

    /// <summary>
    /// 帧率。
    /// </summary>
    public double? FrameRate { get; init; }

    /// <summary>
    /// 时序模式。
    /// </summary>
    public PlaybackTimingMode TimingMode { get; init; }

    /// <summary>
    /// 是否已启用低延迟策略。
    /// </summary>
    public bool LowLatencyEnabled { get; init; }

    /// <summary>
    /// 读取数据包数量。
    /// </summary>
    public long ReadPacketCount { get; init; }

    /// <summary>
    /// 已解码帧数量。
    /// </summary>
    public long DecodedFrameCount { get; init; }

    /// <summary>
    /// 已推送帧数量。
    /// </summary>
    public long PushedFrameCount { get; init; }

    /// <summary>
    /// 视频帧就绪数量。
    /// </summary>
    public long VideoFrameReadyCount { get; init; }

    /// <summary>
    /// 已丢弃非视频包数量。
    /// </summary>
    public long DroppedNonVideoPacketCount { get; init; }

    /// <summary>
    /// 已丢弃帧数量。
    /// </summary>
    public long DroppedFrameCount { get; init; }

    /// <summary>
    /// 缓冲等待决策数量。
    /// </summary>
    public long BufferingDecisionCount { get; init; }

    /// <summary>
    /// 视频缓冲时长。
    /// </summary>
    public TimeSpan? VideoBufferedDuration { get; init; }

    /// <summary>
    /// 是否低延迟捕获完成当前。
    /// </summary>
    public bool LowLatencyCatchUpActive { get; init; }

    /// <summary>
    /// 最近视频音频时间差。
    /// </summary>
    public TimeSpan? LastVideoAudioDistance { get; init; }

    /// <summary>
    /// 最近音频时钟。
    /// </summary>
    public TimeSpan? LastAudioClock { get; init; }

    /// <summary>
    /// 最近帧时刻。
    /// </summary>
    public DateTimeOffset? LastFrameAt { get; init; }

    /// <summary>
    /// 最近界面帧时刻。
    /// </summary>
    public DateTimeOffset? LastUiFrameAt { get; init; }

    /// <summary>
    /// 是否工作线程运行中。
    /// </summary>
    public bool WorkerRunning { get; init; }

    /// <summary>
    /// 工作线程退出原因。
    /// </summary>
    public string? WorkerExitReason { get; init; }

    /// <summary>
    /// 最近 FFmpeg 错误。
    /// </summary>
    public string? LastFFmpegError { get; init; }

    /// <summary>
    /// 最近 FFmpeg 错误代码。
    /// </summary>
    public int? LastFFmpegErrorCode { get; init; }

    /// <summary>
    /// 是否硬件加速请求。
    /// </summary>
    public bool HardwareAccelerationRequested { get; init; }

    /// <summary>
    /// 是否已启用硬件加速。
    /// </summary>
    public bool HardwareAccelerationActive { get; init; }

    /// <summary>
    /// 视频解码器后端。
    /// </summary>
    public string VideoDecoderBackend { get; init; } = "Software";

    /// <summary>
    /// 视频呈现后端。
    /// </summary>
    public string VideoPresentationBackend { get; init; } = "CPU BGRA";

    /// <summary>
    /// 硬件加速失败原因。
    /// </summary>
    public string? HardwareAccelerationFailureReason { get; init; }

    /// <summary>
    /// 是否支持跳转。
    /// </summary>
    public bool CanSeek { get; init; }

    /// <summary>
    /// 网络源是否按直播处理，直播禁止 Seek。
    /// </summary>
    public bool IsLive { get; init; }

    /// <summary>
    /// 是否已知时长。
    /// </summary>
    public bool HasDuration { get; init; }

    /// <summary>
    /// 当前处理到的媒体时间。
    /// </summary>
    public TimeSpan CurrentTime { get; init; }

    /// <summary>
    /// 媒体总时长，未知时为空。
    /// </summary>
    public TimeSpan? Duration { get; init; }

    /// <summary>
    /// 播放速度。
    /// </summary>
    public double PlaybackRate { get; init; } = 1.0;

    /// <summary>
    /// 音量。
    /// </summary>
    public double Volume { get; init; } = 1.0;

    /// <summary>
    /// 是否静音。
    /// </summary>
    public bool IsMuted { get; init; }

    /// <summary>
    /// 是否音频输出可用。
    /// </summary>
    public bool AudioOutputAvailable { get; init; }

    /// <summary>
    /// 音频流索引。
    /// </summary>
    public int? AudioStreamIndex { get; init; }

    /// <summary>
    /// 音频采样率。
    /// </summary>
    public int? AudioSampleRate { get; init; }

    /// <summary>
    /// 音频声道。
    /// </summary>
    public int? AudioChannels { get; init; }

    /// <summary>
    /// 音频输出格式。
    /// </summary>
    public string? AudioOutputFormat { get; init; }

    /// <summary>
    /// 音频工作线程状态。
    /// </summary>
    public string? AudioWorkerState { get; init; }

    /// <summary>
    /// 音频已解码帧数量。
    /// </summary>
    public long AudioDecodedFrameCount { get; init; }

    /// <summary>
    /// 音频已写入 PCM 字节。
    /// </summary>
    public long AudioWrittenPcmBytes { get; init; }

    /// <summary>
    /// 音频当前时间。
    /// </summary>
    public TimeSpan? AudioCurrentTime { get; init; }

    /// <summary>
    /// 视频当前时间。
    /// </summary>
    public TimeSpan? VideoCurrentTime { get; init; }

    /// <summary>
    /// 音频视频偏差。
    /// </summary>
    public TimeSpan? AudioVideoDrift { get; init; }

    /// <summary>
    /// 最近音频错误。
    /// </summary>
    public string? LastAudioError { get; init; }

    /// <summary>
    /// 音频输出设备状态。
    /// </summary>
    public string? AudioOutputDeviceState { get; init; }

    /// <summary>
    /// 最近控制操作。
    /// </summary>
    public string? LastControlOperation { get; init; }

    /// <summary>
    /// 最近控制错误。
    /// </summary>
    public string? LastControlError { get; init; }

    /// <summary>
    /// 生命周期。
    /// </summary>
    public PlaybackLifecycleDiagnosticInfo Lifecycle { get; init; } = new();

    /// <summary>
    /// 健康状态。
    /// </summary>
    public PlaybackHealthDiagnosticInfo Health { get; init; } = new();

    /// <summary>
    /// 网络播放的读取与恢复配置。
    /// </summary>
    public PlaybackNetworkDiagnosticInfo Network { get; init; } = new();

    /// <summary>
    /// 最近跳转。
    /// </summary>
    public PlaybackSeekDiagnosticInfo LastSeek { get; init; } = new();
}
