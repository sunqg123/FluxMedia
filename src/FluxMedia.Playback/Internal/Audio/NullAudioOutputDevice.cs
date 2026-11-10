namespace FluxMedia.Playback.Internal.Audio;

/// <summary>
/// 无可用设备时使用的空音频输出。
/// </summary>
internal sealed class NullAudioOutputDevice : IAudioOutputDevice
{
    /// <summary>
    /// 初始化空音频输出设备所需的状态和资源。
    /// </summary>
    public NullAudioOutputDevice(string reason)
    {
        LastError = reason;
    }

    /// <summary>
    /// 是否可用。
    /// </summary>
    public bool IsAvailable => false;

    /// <summary>
    /// 后端名称。
    /// </summary>
    public string BackendName => "None";

    /// <summary>
    /// 最近错误。
    /// </summary>
    public string? LastError { get; }

    /// <summary>
    /// 当前运行状态。
    /// </summary>
    public AudioOutputState State => AudioOutputState.Unavailable;

    /// <summary>
    /// 获取当前运行状态和诊断信息。
    /// </summary>
    public AudioOutputDiagnosticInfo GetDiagnosticInfo() => new()
    {
        BackendName = BackendName,
        State = State,
        LastError = LastError
    };

    /// <summary>
    /// 记录音频格式并将空输出设备设为就绪。
    /// </summary>
    public void Initialize(int sampleRate, int channels)
    {
    }

    /// <summary>
    /// 启动或恢复音频输出。
    /// </summary>
    public void Play()
    {
    }

    /// <summary>
    /// 暂停音频输出。
    /// </summary>
    public void Pause()
    {
    }

    /// <summary>
    /// 停止当前处理并保留必要的清理状态。
    /// </summary>
    public void Stop()
    {
    }

    /// <summary>
    /// 清空缓存内容并重置相关状态。
    /// </summary>
    public void Clear()
    {
    }

    /// <summary>
    /// 设置音量和静音状态。
    /// </summary>
    public void SetVolume(double volume, bool muted)
    {
    }

    /// <summary>
    /// 获取设备中尚未播放的音频时长。
    /// </summary>
    public TimeSpan GetBufferedDuration() => TimeSpan.Zero;

    /// <summary>
    /// 获取音频设备的输出延迟。
    /// </summary>
    public TimeSpan GetDeviceDelay() => TimeSpan.Zero;

    /// <summary>
    /// 写入数据供后续处理。
    /// </summary>
    public void Write(ReadOnlySpan<byte> pcm)
    {
    }

    /// <summary>
    /// 释放当前对象持有的资源。
    /// </summary>
    public void Dispose()
    {
    }
}
