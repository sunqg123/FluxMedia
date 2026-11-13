namespace FluxMedia.Playback;

/// <summary>
/// 网络读取参数及远程文件、实时流的调度策略。
/// </summary>
public sealed record NetworkPlaybackOptions
{
    /// <summary>
    /// 时序模式。
    /// </summary>
    public PlaybackTimingMode TimingMode { get; init; } = PlaybackTimingMode.Auto;

    /// <summary>
    /// 网络打开超时，直接生效且必须大于零。
    /// </summary>
    public TimeSpan OpenTimeout { get; init; } = TimeSpan.FromSeconds(6);

    /// <summary>
    /// 每次网络读取的超时，直接生效且必须大于零。
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
    /// HTTP(S) 原生重连允许的最大退避间隔，单位为秒。
    /// </summary>
    public int FFmpegReconnectDelayMax { get; init; } = 2;

    /// <summary>
    /// 播放核心两次恢复尝试之间的等待时间。
    /// </summary>
    public TimeSpan ReconnectDelay { get; init; } = TimeSpan.FromSeconds(2);

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

    /// <summary>
    /// 在创建或切源前拒绝无效超时和重连间隔。
    /// </summary>
    internal static void Validate(NetworkPlaybackOptions? options)
    {
        if (options is null)
            throw new ArgumentException("网络配置不能为空。", nameof(PlaybackOptions.Network));
        if (options.OpenTimeout <= TimeSpan.Zero || options.OpenTimeout.TotalMilliseconds > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(OpenTimeout), "网络打开超时必须大于零，且不超过计时器支持的毫秒范围。");
        if (options.ReadTimeout <= TimeSpan.Zero || options.ReadTimeout.TotalMilliseconds > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(ReadTimeout), "网络读取超时必须大于零，且不超过计时器支持的毫秒范围。");
        if (options.FFmpegReconnectDelayMax < 0)
            throw new ArgumentOutOfRangeException(nameof(FFmpegReconnectDelayMax), "FFmpeg 重连间隔上限不能为负数。");
        if (options.ReconnectDelay < TimeSpan.Zero || options.ReconnectDelay.TotalMilliseconds > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(ReconnectDelay), "本地重连间隔不能为负数，且不超过计时器支持的毫秒范围。");
    }
}
