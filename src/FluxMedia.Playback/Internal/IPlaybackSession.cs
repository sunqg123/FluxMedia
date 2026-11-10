using FluxMedia.Core.Configuration;
using FluxMedia.Core.Errors;
using FluxMedia.Core.Models;

namespace FluxMedia.Playback;

/// <summary>
/// 媒体打开、播放控制和音视频输出接口。
/// </summary>
internal interface IPlaybackSession : IAsyncDisposable
{
    /// <summary>
    /// 当前播放器实例标识。
    /// </summary>
    Guid InstanceId { get; }

    /// <summary>
    /// 是否已释放会话。
    /// </summary>
    bool IsDisposed { get; }

    /// <summary>
    /// 停止播放并释放当前管线。
    /// </summary>
    ValueTask StopAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 立即取消等待中的媒体操作，不等待命令锁。
    /// </summary>
    void CancelPendingOperations();

    /// <summary>
    /// 当前运行状态。
    /// </summary>
    PlaybackState State { get; }

    /// <summary>
    /// 当前网络源是否按直播处理。
    /// </summary>
    bool IsLive { get; }

    /// <summary>
    /// 核心是否允许当前媒体调整时间位置。
    /// </summary>
    bool CanSeek { get; }

    /// <summary>
    /// 当前打开的媒体源。
    /// </summary>
    MediaSource? CurrentSource { get; }

    /// <summary>
    /// 当前处理到的媒体时间。
    /// </summary>
    TimeSpan CurrentTime { get; }

    /// <summary>
    /// 媒体总时长，未知时为空。
    /// </summary>
    TimeSpan? Duration { get; }

    /// <summary>
    /// 播放速度。
    /// </summary>
    double PlaybackRate { get; }

    /// <summary>
    /// 音量。
    /// </summary>
    double Volume { get; }

    /// <summary>
    /// 是否静音。
    /// </summary>
    bool IsMuted { get; }

    /// <summary>
    /// 组件运行状态变化时发出的通知。
    /// </summary>
    event EventHandler<PlaybackState>? StateChanged;

    /// <summary>
    /// 发生运行错误时发出的通知。
    /// </summary>
    event EventHandler<FluxMediaError>? Faulted;

    /// <summary>
    /// 播放位置变化时发出的通知。
    /// </summary>
    event EventHandler<PlaybackPositionChangedEventArgs>? PositionChanged;

    /// <summary>
    /// 新视频帧可供显示时发出的通知。
    /// </summary>
    event EventHandler<PlaybackVideoFrameEventArgs>? VideoFrameReady;

    /// <summary>
    /// 更新显示端支持的 GPU 输出能力。
    /// </summary>
    void ConfigureVideoOutput(VideoOutputCapabilities capabilities);

    /// <summary>
    /// 打开媒体源并读取媒体信息。
    /// </summary>
    ValueTask OpenAsync(PlaybackOpenRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// 获取当前运行状态和诊断信息。
    /// </summary>
    PlaybackDiagnosticInfo GetDiagnosticInfo();

    /// <summary>
    /// 启动解码和音频输出，结束后可重新播放。
    /// </summary>
    ValueTask<PlaybackResult> PlayAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 暂停播放并保留当前位置。
    /// </summary>
    ValueTask<PlaybackResult> PauseAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 恢复暂停的播放。
    /// </summary>
    ValueTask<PlaybackResult> ResumeAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 跳转到指定媒体时间。
    /// </summary>
    ValueTask<PlaybackResult> SeekAsync(TimeSpan position, CancellationToken cancellationToken = default);

    /// <summary>
    /// 设置播放速度。
    /// </summary>
    ValueTask<PlaybackResult> SetPlaybackRateAsync(double rate, CancellationToken cancellationToken = default);

    /// <summary>
    /// 设置音量，范围为 0 到 1。
    /// </summary>
    ValueTask<PlaybackResult> SetVolumeAsync(double volume, CancellationToken cancellationToken = default);

    /// <summary>
    /// 设置静音状态。
    /// </summary>
    ValueTask<PlaybackResult> SetMutedAsync(bool muted, CancellationToken cancellationToken = default);

    /// <summary>
    /// 开始录制当前播放的数据包。
    /// </summary>
    ValueTask<PlaybackResult> StartRecordingAsync(PlaybackRecordingOptions options, CancellationToken cancellationToken = default);

    /// <summary>
    /// 停止录制并封装输出文件。
    /// </summary>
    ValueTask<PlaybackResult> StopRecordingAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 获取当前录制状态和错误信息。
    /// </summary>
    PlaybackRecordingDiagnosticInfo GetRecordingDiagnosticInfo();

    /// <summary>
    /// 获取已打开媒体的基本信息。
    /// </summary>
    ValueTask<MediaInfo> GetMediaInfoAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 依次读取视频帧；跨迭代使用时需保留租约。
    /// </summary>
    IAsyncEnumerable<VideoFrame> ReadVideoFramesAsync(CancellationToken cancellationToken = default);
}
