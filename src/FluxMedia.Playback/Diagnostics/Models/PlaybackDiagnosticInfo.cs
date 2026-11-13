using FluxMedia.Core.Models;

namespace FluxMedia.Playback;

/// <summary>
/// 播放诊断信息。
/// </summary>
public sealed record PlaybackDiagnosticInfo
{
    /// <summary>
    /// 本次快照读取到的播放状态。
    /// </summary>
    public PlaybackState State { get; init; }

    /// <summary>
    /// 源类型。
    /// </summary>
    public MediaSourceKind? SourceKind { get; init; }

    /// <summary>
    /// 源地址。
    /// </summary>
    public string? SourceUrl { get; init; }

    /// <summary>
    /// 按当前配置生成的 FFmpeg 打开参数。
    /// </summary>
    public string? FFmpegOpenOptions { get; init; }

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
    public PlaybackTimingMode? TimingMode { get; init; }

    /// <summary>
    /// 是否已启用低延迟策略。
    /// </summary>
    public bool? LowLatencyEnabled { get; init; }

    /// <summary>
    /// 视频缓冲时长。
    /// </summary>
    public TimeSpan? VideoBufferedDuration { get; init; }

    /// <summary>
    /// 是否硬件加速请求。
    /// </summary>
    public bool HardwareAccelerationRequested { get; init; }

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
    /// 最近请求的目标倍速。
    /// </summary>
    public double RequestedRate { get; init; } = 1.0;

    /// <summary>
    /// 当前处理链已提交的实际倍速。
    /// </summary>
    public double AppliedRate { get; init; } = 1.0;

    /// <summary>
    /// 当前配置允许的最低倍速。
    /// </summary>
    public double MinRate { get; init; }

    /// <summary>
    /// 当前配置允许的最高倍速。
    /// </summary>
    public double MaxRate { get; init; }

    /// <summary>
    /// 最近换速请求编号。
    /// </summary>
    public long RateRequestId { get; init; }

    /// <summary>
    /// 最近换速请求的结果，不代表当前播放是否正常。
    /// </summary>
    public string RateChangeState { get; init; } = "Applied";

    /// <summary>
    /// 是否仍有倍速请求等待应用。
    /// </summary>
    public bool IsSpeedChangePending { get; init; }

    /// <summary>
    /// 最近换速失败原因。
    /// </summary>
    public string? LastRateError { get; init; }

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
    /// 媒体音轨的采样率。
    /// </summary>
    public int? AudioSampleRate { get; init; }

    /// <summary>
    /// 媒体音轨的声道数。
    /// </summary>
    public int? AudioChannels { get; init; }

    /// <summary>
    /// 音频时钟快照中的可听位置。
    /// </summary>
    public TimeSpan? AudioCurrentTime { get; init; }

    /// <summary>
    /// 视频当前时间。
    /// </summary>
    public TimeSpan? VideoCurrentTime { get; init; }

    /// <summary>
    /// 音频输出设备状态。
    /// </summary>
    public string? AudioOutputDeviceState { get; init; }

    /// <summary>
    /// 健康状态。
    /// </summary>
    public PlaybackHealthDiagnosticInfo? Health { get; init; }

    /// <summary>
    /// 网络播放的读取与恢复配置。
    /// </summary>
    public PlaybackNetworkDiagnosticInfo? Network { get; init; }

    /// <summary>
    /// 最近跳转。
    /// </summary>
    public PlaybackSeekDiagnosticInfo? LastSeek { get; init; }
}
