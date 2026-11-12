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
    /// 初始点播速度，超出配置范围或预算时回退 1 倍；直播固定为 1。
    /// </summary>
    public double InitialPlaybackRate { get; init; } = 1.0;

    /// <summary>
    /// 播放中等待换速准备的超时，暂停待应用请求不计时。
    /// </summary>
    public TimeSpan PlaybackRateTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// 不可逆换速提交的看门狗时长，超时按管线故障处理。
    /// </summary>
    public TimeSpan PlaybackRateCommitTimeout { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// 是否以静音状态开始。
    /// </summary>
    public bool InitialMuted { get; init; }

    /// <summary>
    /// 硬件解码的启用策略。
    /// </summary>
    public VideoAccelerationMode VideoAcceleration { get; init; } = VideoAccelerationMode.Auto;
}
