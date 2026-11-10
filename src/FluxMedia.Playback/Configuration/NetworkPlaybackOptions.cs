namespace FluxMedia.Playback;

/// <summary>
/// 网络读取参数及远程文件、实时流的调度策略。
/// </summary>
public sealed record NetworkPlaybackOptions
{
    /// <summary>
    /// 模式。
    /// </summary>
    public NetworkPlaybackMode Mode { get; init; } = NetworkPlaybackMode.LowLatency;

    /// <summary>
    /// 时序模式。
    /// </summary>
    public PlaybackTimingMode TimingMode { get; init; } = PlaybackTimingMode.Auto;

    /// <summary>
    /// 打开超时。
    /// </summary>
    public TimeSpan OpenTimeout { get; init; } = TimeSpan.FromSeconds(6);

    /// <summary>
    /// 读取超时。
    /// </summary>
    public TimeSpan ReadTimeout { get; init; } = TimeSpan.FromSeconds(4);

    /// <summary>
    /// 探测大小。
    /// </summary>
    public long ProbeSize { get; init; } = 16 * 1024;

    /// <summary>
    /// 分析时长。
    /// </summary>
    public TimeSpan AnalyzeDuration { get; init; } = TimeSpan.FromMilliseconds(150);

    /// <summary>
    /// RTSP 传输。
    /// </summary>
    public RtspTransportMode RtspTransport { get; init; } = RtspTransportMode.Tcp;

    /// <summary>
    /// 是否重连已启用。
    /// </summary>
    public bool ReconnectEnabled { get; init; } = true;

    /// <summary>
    /// 重连延迟最大。
    /// </summary>
    public int ReconnectDelayMax { get; init; } = 2;

    /// <summary>
    /// 是否低延迟模式。
    /// </summary>
    public bool LowLatencyMode { get; init; } = true;

    /// <summary>
    /// 最大网络帧延迟。
    /// </summary>
    public TimeSpan MaxNetworkFrameDelay { get; init; } = TimeSpan.FromMilliseconds(60);

    /// <summary>
    /// 创建偏重兼容性的网络播放配置。
    /// </summary>
    public static NetworkPlaybackOptions Compatible() => new()
    {
        Mode = NetworkPlaybackMode.Compatible,
        OpenTimeout = TimeSpan.FromSeconds(12),
        ReadTimeout = TimeSpan.FromSeconds(8),
        ProbeSize = 256 * 1024,
        AnalyzeDuration = TimeSpan.FromSeconds(1),
        LowLatencyMode = false,
        MaxNetworkFrameDelay = TimeSpan.FromMilliseconds(120)
    };

    /// <summary>
    /// 创建偏重低延迟的网络播放配置。
    /// </summary>
    public static NetworkPlaybackOptions LowLatency() => new();
}
