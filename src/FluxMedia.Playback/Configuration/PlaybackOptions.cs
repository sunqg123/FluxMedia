using FluxMedia.Core.Models;

namespace FluxMedia.Playback;

/// <summary>
/// 播放会话配置。
/// </summary>
public sealed record PlaybackOptions
{
    /// <summary>
    /// 打开本地媒体的超时时间。
    /// </summary>
    public TimeSpan OpenTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// 网络操作的默认超时时间。
    /// </summary>
    public TimeSpan NetworkTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// 网络播放的最大恢复次数。
    /// </summary>
    public int NetworkReconnectAttempts { get; init; } = 3;

    /// <summary>
    /// 网络读取和低延迟参数。
    /// </summary>
    public NetworkPlaybackOptions Network { get; init; } = NetworkPlaybackOptions.LowLatency();

    /// <summary>
    /// 初始音量，范围为 0 到 1。
    /// </summary>
    public double InitialVolume { get; init; } = 1.0;

    /// <summary>
    /// 初始点播速度为 0.5 到 2，直播固定为 1。
    /// </summary>
    public double InitialPlaybackRate { get; init; } = 1.0;

    /// <summary>
    /// 是否以静音状态开始。
    /// </summary>
    public bool InitialMuted { get; init; }

    /// <summary>
    /// 硬件解码的启用策略。
    /// </summary>
    public VideoAccelerationMode VideoAcceleration { get; init; } = VideoAccelerationMode.Auto;
}

/// <summary>
/// 视频硬件加速模式。
/// </summary>
public enum VideoAccelerationMode
{
    /// <summary>
    /// 已禁用。
    /// </summary>
    Disabled,
    /// <summary>
    /// 根据媒体和平台自动选择。
    /// </summary>
    Auto,
    /// <summary>
    /// 要求硬件。
    /// </summary>
    RequireHardware
}

/// <summary>
/// 显示端支持的 GPU 共享能力。
/// </summary>
public sealed record VideoOutputCapabilities
{
    /// <summary>
    /// 显示端能否导入 D3D11 共享纹理。
    /// </summary>
    public bool SupportsD3D11SharedTexture { get; init; }

    /// <summary>
    /// 显示端能否导入 Vulkan 文件描述符。
    /// </summary>
    public bool SupportsVulkanOpaqueFileDescriptor { get; init; }

    /// <summary>
    /// 显示设备的适配器标识。
    /// </summary>
    public byte[]? AdapterLuid { get; init; }

    /// <summary>
    /// 显示设备的唯一标识。
    /// </summary>
    public byte[]? DeviceUuid { get; init; }
}

/// <summary>
/// 网络播放的兼容或低延迟模式。
/// </summary>
public enum NetworkPlaybackMode
{
    /// <summary>
    /// 兼容。
    /// </summary>
    Compatible,
    /// <summary>
    /// 低延迟。
    /// </summary>
    LowLatency
}

/// <summary>
/// RTSP 使用的传输协议。
/// </summary>
public enum RtspTransportMode
{
    /// <summary>
    /// TCP。
    /// </summary>
    Tcp,
    /// <summary>
    /// UDP。
    /// </summary>
    Udp
}

/// <summary>
/// 视频帧的播放调度模式。
/// </summary>
public enum PlaybackTimingMode
{
    /// <summary>
    /// 根据媒体和平台自动选择。
    /// </summary>
    Auto,
    /// <summary>
    /// 呈现时间戳。
    /// </summary>
    PresentationTimestamp,
    /// <summary>
    /// 实时低延迟。
    /// </summary>
    RealtimeLowLatency
}

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

/// <summary>
/// 打开媒体源的请求。
/// </summary>
/// <param name="Source">需要打开的媒体源。</param>
/// <param name="Options">本次打开媒体采用的播放配置。</param>
public sealed record PlaybackOpenRequest(MediaSource Source, PlaybackOptions? Options = null);
