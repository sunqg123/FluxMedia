namespace FluxMedia.Playback.Internal.Audio;

/// <summary>
/// 平台音频设备契约，播放管线只提交 PCM。
/// </summary>
internal interface IAudioOutputDevice : IDisposable
{
    /// <summary>
    /// 是否可用。
    /// </summary>
    bool IsAvailable
    {
        get;
    }

    /// <summary>
    /// 后端名称。
    /// </summary>
    string BackendName
    {
        get;
    }

    /// <summary>
    /// 最近错误。
    /// </summary>
    string? LastError
    {
        get;
    }

    /// <summary>
    /// 当前运行状态。
    /// </summary>
    AudioOutputState State
    {
        get;
    }

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
