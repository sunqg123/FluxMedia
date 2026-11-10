using FluxMedia.Core.Models;

namespace FluxMedia.Playback.Internal;

/// <summary>
/// 视频解码线程的运行状态和帧统计。
/// </summary>
internal sealed record PlaybackWorkerDiagnostics
{
    /// <summary>
    /// 源类型。
    /// </summary>
    public MediaSourceKind SourceKind { get; init; }

    /// <summary>
    /// 源地址。
    /// </summary>
    public string? SourceUrl { get; init; }

    /// <summary>
    /// 打开配置。
    /// </summary>
    public string? OpenOptions { get; init; }

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
    /// 已丢弃视频帧数量。
    /// </summary>
    public long DroppedVideoFrameCount { get; init; }

    /// <summary>
    /// 缓冲等待决策数量。
    /// </summary>
    public long BufferingDecisionCount { get; init; }

    /// <summary>
    /// 最近视频缓冲时长。
    /// </summary>
    public TimeSpan? LastVideoBufferedDuration { get; init; }

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
    /// 已丢弃非视频包数量。
    /// </summary>
    public long DroppedNonVideoPacketCount { get; init; }

    /// <summary>
    /// 最近帧时刻。
    /// </summary>
    public DateTimeOffset? LastFrameAt { get; init; }

    /// <summary>
    /// 是否工作线程运行中。
    /// </summary>
    public bool WorkerRunning { get; init; }

    /// <summary>
    /// 退出原因。
    /// </summary>
    public string? ExitReason { get; init; }

    /// <summary>
    /// 最近 FFmpeg 错误代码。
    /// </summary>
    public int? LastFFmpegErrorCode { get; init; }

    /// <summary>
    /// 最近 FFmpeg 错误。
    /// </summary>
    public string? LastFFmpegError { get; init; }

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
}
