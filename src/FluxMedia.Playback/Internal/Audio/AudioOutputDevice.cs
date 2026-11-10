namespace FluxMedia.Playback.Internal.Audio;

// 当前项目承载统一音频输出抽象、Windows NAudio 后端和 Linux OpenAL 后端。

/// <summary>
/// 播放器音频输出设备状态。
/// </summary>
internal enum AudioOutputState
{
    /// <summary>
    /// 已创建。
    /// </summary>
    Created,
    /// <summary>
    /// 已初始化。
    /// </summary>
    Initialized,
    /// <summary>
    /// 播放中。
    /// </summary>
    Playing,
    /// <summary>
    /// 已暂停。
    /// </summary>
    Paused,
    /// <summary>
    /// 已停止。
    /// </summary>
    Stopped,
    /// <summary>
    /// 失败。
    /// </summary>
    Failed,
    /// <summary>
    /// 已释放。
    /// </summary>
    Disposed,
    /// <summary>
    /// 不可用。
    /// </summary>
    Unavailable
}


/// <summary>
/// PCM 输出格式描述。
/// </summary>
/// <param name="SampleRate">音频采样率，单位为赫兹。</param>
/// <param name="Channels">音频声道数。</param>
/// <param name="BitsPerSample">每个音频采样使用的位数。</param>
internal sealed record PcmAudioFormat(int SampleRate, int Channels, int BitsPerSample = 16);


/// <summary>
/// 音频输出诊断信息。
/// </summary>
internal sealed record AudioOutputDiagnosticInfo
{
    /// <summary>
    /// 后端名称。
    /// </summary>
    public string BackendName { get; init; } = "None";

    /// <summary>
    /// 当前运行状态。
    /// </summary>
    public AudioOutputState State { get; init; } = AudioOutputState.Created;

    /// <summary>
    /// 格式。
    /// </summary>
    public PcmAudioFormat? Format { get; init; }

    /// <summary>
    /// 已写入字节。
    /// </summary>
    public long WrittenBytes { get; init; }

    /// <summary>
    /// 最近错误。
    /// </summary>
    public string? LastError { get; init; }

    /// <summary>
    /// 是否静音。
    /// </summary>
    public bool IsMuted { get; init; }

    /// <summary>
    /// 音量。
    /// </summary>
    public double Volume { get; init; }

    /// <summary>
    /// 缓冲时长。
    /// </summary>
    public TimeSpan BufferedDuration { get; init; }

    /// <summary>
    /// 设备延迟。
    /// </summary>
    public TimeSpan DeviceDelay { get; init; }

}


/// <summary>
/// 平台音频设备契约，播放管线只提交 PCM。
/// </summary>
internal interface IAudioOutputDevice : IDisposable
{
    /// <summary>
    /// 是否可用。
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>
    /// 后端名称。
    /// </summary>
    string BackendName { get; }

    /// <summary>
    /// 最近错误。
    /// </summary>
    string? LastError { get; }

    /// <summary>
    /// 当前运行状态。
    /// </summary>
    AudioOutputState State { get; }

    /// <summary>
    /// 获取当前运行状态和诊断信息。
    /// </summary>
    AudioOutputDiagnosticInfo GetDiagnosticInfo();

    /// <summary>
    /// 按采样率和声道数初始化 PCM 音频输出。
    /// </summary>
    void Initialize(int sampleRate, int channels);

    /// <summary>
    /// 启动或恢复音频输出。
    /// </summary>
    void Play();

    /// <summary>
    /// 暂停音频输出。
    /// </summary>
    void Pause();

    /// <summary>
    /// 停止当前处理并保留必要的清理状态。
    /// </summary>
    void Stop();

    /// <summary>
    /// 清空缓存内容并重置相关状态。
    /// </summary>
    void Clear();

    /// <summary>
    /// 设置音量和静音状态。
    /// </summary>
    void SetVolume(double volume, bool muted);

    /// <summary>
    /// 获取设备中尚未播放的音频时长。
    /// </summary>
    TimeSpan GetBufferedDuration();

    /// <summary>
    /// 获取音频设备的输出延迟。
    /// </summary>
    TimeSpan GetDeviceDelay();

    /// <summary>
    /// 写入数据供后续处理。
    /// </summary>
    void Write(ReadOnlySpan<byte> pcm);
}


/// <summary>
/// 统计音频缓冲区的复用情况。
/// </summary>
internal interface IPooledAudioOutputDevice
{
    /// <summary>
    /// 写入数据供后续处理。
    /// </summary>
    void Write(byte[] pcm, int length);
}
