using FluxMedia.Core.Configuration;
using FluxMedia.Core.Logging;
using NAudio.Wave;

namespace FluxMedia.Playback.Internal.Audio;

/// <summary>
/// Windows NAudio 输出实现。
/// </summary>
internal sealed class NAudioPlaybackOutput : IAudioOutputDevice, IPooledAudioOutputDevice
{

    /// <summary>
    /// 实例标识。
    /// </summary>
    private readonly Guid _instanceId;

    /// <summary>
    /// Windows 平台使用的 NAudio 播放设备。
    /// </summary>
    private WaveOutEvent? _waveOut;

    /// <summary>
    /// 供播放设备读取的 PCM 音频缓冲。
    /// </summary>
    private BufferedWaveProvider? _buffer;

    /// <summary>
    /// 当前 Windows 音频输出采用的 PCM 格式。
    /// </summary>
    private PcmAudioFormat? _format;

    /// <summary>
    /// 已写入字节。
    /// </summary>
    private long _writtenBytes;

    /// <summary>
    /// 音频设备期望的缓冲延迟（毫秒）。
    /// </summary>
    private const int DesiredLatencyMs = 100;

    /// <summary>
    /// 当前音量，范围为 0 到 1。
    /// </summary>
    private double _volume = 1;

    /// <summary>
    /// 是否静音。
    /// </summary>
    private bool _muted;

    /// <summary>
    /// 状态。
    /// </summary>
    private AudioOutputState _state = AudioOutputState.Created;

    /// <summary>
    /// 初始化 NAudio 播放输出所需的状态和资源。
    /// </summary>
    public NAudioPlaybackOutput(Guid instanceId)
    {
        _instanceId = instanceId;
    }

    /// <summary>
    /// 是否可用。
    /// </summary>
    public bool IsAvailable => _state is AudioOutputState.Initialized or AudioOutputState.Playing or AudioOutputState.Paused or AudioOutputState.Stopped;

    /// <summary>
    /// 后端名称。
    /// </summary>
    public string BackendName => "NAudio.WaveOutEvent";

    /// <summary>
    /// 最近错误。
    /// </summary>
    public string? LastError { get; private set; }

    /// <summary>
    /// 当前运行状态。
    /// </summary>
    public AudioOutputState State => _state;

    /// <summary>
    /// 获取当前运行状态和诊断信息。
    /// </summary>
    public AudioOutputDiagnosticInfo GetDiagnosticInfo() => new()
    {
        BackendName = BackendName,
        State = _state,
        Format = _format,
        WrittenBytes = Interlocked.Read(ref _writtenBytes),
        LastError = LastError,
        IsMuted = _muted,
        Volume = _volume,
        BufferedDuration = GetBufferedDuration(),
        DeviceDelay = GetDeviceDelay()
    };

    /// <summary>
    /// 按 PCM 格式创建 Windows 音频播放设备。
    /// </summary>
    public void Initialize(int sampleRate, int channels)
    {
        if (IsAvailable) return;
        try
        {
            var waveFormat = new WaveFormat(sampleRate, 16, channels);
            _buffer = new BufferedWaveProvider(waveFormat)
            {
                BufferDuration = TimeSpan.FromMilliseconds(750),
                DiscardOnBufferOverflow = false
            };
            _waveOut = new WaveOutEvent
            {
                DesiredLatency = DesiredLatencyMs
            };
            _waveOut.PlaybackStopped += OnPlaybackStopped;
            _waveOut.Init(_buffer);
            _format = new PcmAudioFormat(sampleRate, channels);
            _state = AudioOutputState.Initialized;
            FluxMediaLog.Write(LogLevel.Information, nameof(NAudioPlaybackOutput), _instanceId, $"Event=AudioOutputInitialized Backend={BackendName} SampleRate={sampleRate} Channels={channels}");
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            _state = AudioOutputState.Failed;
            FluxMediaLog.Write(LogLevel.Error, nameof(NAudioPlaybackOutput), _instanceId, $"Event=AudioOutputInitFailed Backend={BackendName} SampleRate={sampleRate} Channels={channels} Error={ex.Message}", ex);
            Dispose();
        }
    }

    /// <summary>
    /// 启动或恢复音频输出。
    /// </summary>
    public void Play()
    {
        _waveOut?.Play();
        if (IsAvailable)
        {
            _state = AudioOutputState.Playing;
        }
    }

    /// <summary>
    /// 暂停音频输出。
    /// </summary>
    public void Pause()
    {
        _waveOut?.Pause();
        if (IsAvailable)
        {
            _state = AudioOutputState.Paused;
        }
    }

    /// <summary>
    /// 停止当前处理并保留必要的清理状态。
    /// </summary>
    public void Stop()
    {
        _waveOut?.Stop();
        if (IsAvailable)
        {
            _state = AudioOutputState.Stopped;
        }
    }

    /// <summary>
    /// 清空缓存内容并重置相关状态。
    /// </summary>
    public void Clear()
    {
        _buffer?.ClearBuffer();
    }

    /// <summary>
    /// 设置音量和静音状态。
    /// </summary>
    public void SetVolume(double volume, bool muted)
    {
        _volume = Math.Clamp(volume, 0, 1);
        _muted = muted;
        if (_waveOut is not null)
        {
            _waveOut.Volume = muted ? 0f : (float)_volume;
        }
    }

    /// <summary>
    /// 获取设备中尚未播放的音频时长。
    /// </summary>
    public TimeSpan GetBufferedDuration()
    {
        return _buffer?.BufferedDuration ?? TimeSpan.Zero;
    }

    /// <summary>
    /// 获取音频设备的输出延迟。
    /// </summary>
    public TimeSpan GetDeviceDelay()
    {
        return IsAvailable ? TimeSpan.FromMilliseconds(DesiredLatencyMs) : TimeSpan.Zero;
    }

    /// <summary>
    /// 写入数据供后续处理。
    /// </summary>
    public void Write(ReadOnlySpan<byte> pcm)
    {
        if (pcm.IsEmpty) return;
        var data = pcm.ToArray();
        Write(data, data.Length);
    }

    /// <summary>
    /// 写入数据供后续处理。
    /// </summary>
    void IPooledAudioOutputDevice.Write(byte[] pcm, int length) => Write(pcm, length);

    /// <summary>
    /// 写入数据供后续处理。
    /// </summary>
    private void Write(byte[] data, int length)
    {
        var buffer = _buffer;
        if (buffer is null || length <= 0) return;
        while (_state is AudioOutputState.Initialized or AudioOutputState.Playing or AudioOutputState.Paused)
        {
            if (buffer.BufferLength - buffer.BufferedBytes >= length)
            {
                buffer.AddSamples(data, 0, length);
                Interlocked.Add(ref _writtenBytes, length);
                return;
            }

            Thread.Sleep(5);
        }
        // Stop / Dispose 时立即结束等待，避免音频缓冲阻塞播放器退出。
        return;
    }

    /// <summary>
    /// 释放当前对象持有的资源。
    /// </summary>
    public void Dispose()
    {
        try
        {
            _waveOut?.Stop();
        }
        catch
        {
        }

        if (_waveOut is not null)
        {
            _waveOut.PlaybackStopped -= OnPlaybackStopped;
            _waveOut.Dispose();
        }
        _waveOut = null;
        _buffer = null;
        _state = AudioOutputState.Disposed;
    }

    /// <summary>
    /// 播放设备停止时记录状态和可选异常。
    /// </summary>
    private void OnPlaybackStopped(object? sender, StoppedEventArgs args)
    {
        if (args.Exception is null || _state is AudioOutputState.Stopped or AudioOutputState.Disposed) return;
        LastError = args.Exception.Message;
        _state = AudioOutputState.Failed;
        FluxMediaLog.Write(
            LogLevel.Error, nameof(NAudioPlaybackOutput), _instanceId, $"Event=AudioOutputPlaybackStoppedUnexpectedly Backend={BackendName} Error={args.Exception.Message}", args.Exception);
    }
}
