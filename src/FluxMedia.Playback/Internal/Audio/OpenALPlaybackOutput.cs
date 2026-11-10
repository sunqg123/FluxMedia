using System.Runtime.InteropServices;
using FluxMedia.Core.Configuration;
using FluxMedia.Core.Logging;

namespace FluxMedia.Playback.Internal.Audio;

/// <summary>
/// Linux OpenAL 流式 PCM 输出实现。应用需要安装提供 libopenal.so.1 的 OpenAL 运行库。
/// </summary>
internal sealed class OpenALPlaybackOutput : IAudioOutputDevice, IPooledAudioOutputDevice
{
    /// <summary>
    /// 最大已排队缓冲区。
    /// </summary>
    private const int MaxQueuedBuffers = 16;

    /// <summary>
    /// OpenAL 无错误状态。
    /// </summary>
    private const int AlNoError = 0;

    /// <summary>
    /// OpenAL 16 位单声道格式。
    /// </summary>
    private const int AlFormatMono16 = 0x1101;

    /// <summary>
    /// OpenAL 16 位双声道格式。
    /// </summary>
    private const int AlFormatStereo16 = 0x1103;

    /// <summary>
    /// OpenAL 音量参数。
    /// </summary>
    private const int AlGain = 0x100A;

    /// <summary>
    /// OpenAL 音频源状态参数。
    /// </summary>
    private const int AlSourceState = 0x1010;

    /// <summary>
    /// OpenAL 播放中状态。
    /// </summary>
    private const int AlPlaying = 0x1012;

    /// <summary>
    /// OpenAL 已排队缓冲数参数。
    /// </summary>
    private const int AlBuffersQueued = 0x1015;

    /// <summary>
    /// OpenAL 已处理缓冲数参数。
    /// </summary>
    private const int AlBuffersProcessed = 0x1016;

    /// <summary>
    /// 保护组件共享状态的同步锁。
    /// </summary>
    private readonly object _syncRoot = new();

    /// <summary>
    /// 实例标识。
    /// </summary>
    private readonly Guid _instanceId;

    /// <summary>
    /// 当前打开的 OpenAL 音频设备句柄。
    /// </summary>
    private IntPtr _device;

    /// <summary>
    /// 当前音频设备的 OpenAL 上下文句柄。
    /// </summary>
    private IntPtr _context;

    /// <summary>
    /// OpenAL 音频源的原生标识。
    /// </summary>
    private uint _source;

    /// <summary>
    /// 当前上传 PCM 数据所采用的音频格式。
    /// </summary>
    private PcmAudioFormat? _format;

    /// <summary>
    /// 已写入字节。
    /// </summary>
    private long _writtenBytes;

    /// <summary>
    /// 当前音频设备中尚未回收的字节数。
    /// </summary>
    private long _queuedBytes;

    /// <summary>
    /// 按音频缓冲区句柄记录的有效字节数。
    /// </summary>
    private readonly Queue<int> _queuedBufferSizes = new();

    /// <summary>
    /// 当前音量，范围为 0 到 1。
    /// </summary>
    private double _volume = 1;

    /// <summary>
    /// 是否静音。
    /// </summary>
    private bool _muted;

    /// <summary>
    /// 是否已释放资源。
    /// </summary>
    private bool _disposed;

    /// <summary>
    /// 当前音频输出的播放状态。
    /// </summary>
    private AudioOutputState _state = AudioOutputState.Created;

    /// <summary>
    /// 初始化 OpenAL 播放输出所需的状态和资源。
    /// </summary>
    public OpenALPlaybackOutput(Guid instanceId)
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
    public string BackendName => "OpenAL";

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
    public AudioOutputDiagnosticInfo GetDiagnosticInfo()
    {
        lock (_syncRoot)
        {
            return new AudioOutputDiagnosticInfo
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
        }
    }

    /// <summary>
    /// 打开 OpenAL 设备并创建音频源和缓冲区。
    /// </summary>
    public void Initialize(int sampleRate, int channels)
    {
        lock (_syncRoot)
        {
            if (_disposed || IsAvailable) return;
            if (sampleRate <= 0)
            {
                Fail($"无效采样率：{sampleRate}");
                return;
            }

            if (channels is not 1 and not 2)
            {
                Fail($"OpenAL 当前仅支持单声道或双声道 PCM，实际声道数：{channels}");
                return;
            }

            try
            {
                _device = OpenALNative.AlcOpenDevice(IntPtr.Zero);
                if (_device == IntPtr.Zero)
                {
                    throw new InvalidOperationException("OpenAL 无法打开默认音频设备。");
                }

                _context = OpenALNative.AlcCreateContext(_device, IntPtr.Zero);
                if (_context == IntPtr.Zero)
                {
                    throw new InvalidOperationException("OpenAL 无法创建音频上下文。");
                }

                if (OpenALNative.AlcMakeContextCurrent(_context) == 0)
                {
                    throw new InvalidOperationException("OpenAL 无法激活音频上下文。");
                }

                OpenALNative.AlGenSources(1, out _source);
                ThrowIfAlError("alGenSources");
                _format = new PcmAudioFormat(sampleRate, channels);
                _state = AudioOutputState.Initialized;
                ApplyVolume();
                FluxMediaLog.Write(
                    LogLevel.Information, nameof(OpenALPlaybackOutput), _instanceId, $"Event=AudioOutputInitialized Backend={BackendName} SampleRate={sampleRate} Channels={channels}");
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                _state = AudioOutputState.Failed;
                FluxMediaLog.Write(
                    LogLevel.Error, nameof(OpenALPlaybackOutput), _instanceId, $"Event=AudioOutputInitFailed Backend={BackendName} SampleRate={sampleRate} Channels={channels} Error={ex.Message}", ex);
                ReleaseNativeResources();
            }
        }
    }

    /// <summary>
    /// 启动或恢复音频输出。
    /// </summary>
    public void Play()
    {
        lock (_syncRoot)
        {
            if (!PrepareOperation()) return;
            OpenALNative.AlSourcePlay(_source);
            ThrowIfAlError("alSourcePlay");
            _state = AudioOutputState.Playing;
        }
    }

    /// <summary>
    /// 暂停音频输出。
    /// </summary>
    public void Pause()
    {
        lock (_syncRoot)
        {
            if (!PrepareOperation()) return;
            OpenALNative.AlSourcePause(_source);
            ThrowIfAlError("alSourcePause");
            _state = AudioOutputState.Paused;
        }
    }

    /// <summary>
    /// 停止当前处理并保留必要的清理状态。
    /// </summary>
    public void Stop()
    {
        lock (_syncRoot)
        {
            if (!PrepareOperation()) return;
            OpenALNative.AlSourceStop(_source);
            ThrowIfAlError("alSourceStop");
            ClearQueuedBuffers();
            _state = AudioOutputState.Stopped;
        }
    }

    /// <summary>
    /// 清空缓存内容并重置相关状态。
    /// </summary>
    public void Clear()
    {
        lock (_syncRoot)
        {
            if (!PrepareOperation()) return;
            OpenALNative.AlSourceStop(_source);
            ThrowIfAlError("alSourceStop");
            ClearQueuedBuffers();
            _state = AudioOutputState.Initialized;
        }
    }

    /// <summary>
    /// 设置音量和静音状态。
    /// </summary>
    public void SetVolume(double volume, bool muted)
    {
        lock (_syncRoot)
        {
            _volume = Math.Clamp(volume, 0, 1);
            _muted = muted;
            if (PrepareOperation())
            {
                ApplyVolume();
            }
        }
    }

    /// <summary>
    /// 写入数据供后续处理。
    /// </summary>
    public void Write(ReadOnlySpan<byte> pcm)
    {
        if (pcm.IsEmpty)
        {
            return;
        }

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
    private void Write(byte[] pcm, int length)
    {
        if (length <= 0) return;
        while (true)
        {
            lock (_syncRoot)
            {
                if (!PrepareOperation() || _format is null) return;
                ReclaimProcessedBuffers();
                OpenALNative.AlGetSourcei(_source, AlBuffersQueued, out var queued);
                ThrowIfAlError("alGetSourcei(AL_BUFFERS_QUEUED)");
                if (queued < MaxQueuedBuffers)
                {
                    QueuePcmBuffer(pcm, length);
                    EnsurePlaying();
                    Interlocked.Add(ref _writtenBytes, length);
                    return;
                }
            }
            Thread.Sleep(5);
        }
    }

    /// <summary>
    /// 获取设备中尚未播放的音频时长。
    /// </summary>
    public TimeSpan GetBufferedDuration()
    {
        lock (_syncRoot)
        {
            return DurationFromBytes(Interlocked.Read(ref _queuedBytes));
        }
    }

    /// <summary>
    /// 获取音频设备的输出延迟。
    /// </summary>
    public TimeSpan GetDeviceDelay()
    {
        return _state == AudioOutputState.Playing ? TimeSpan.FromMilliseconds(40) : TimeSpan.Zero;
    }

    /// <summary>
    /// 释放当前对象持有的资源。
    /// </summary>
    public void Dispose()
    {
        lock (_syncRoot)
        {
            if (_disposed) return;
            _disposed = true;
            try
            {
                if (_source != 0 && _context != IntPtr.Zero)
                {
                    EnsureContextCurrent();
                    OpenALNative.AlSourceStop(_source);
                    ClearQueuedBuffers();
                }
            }
            catch (Exception ex)
            {
                FluxMediaLog.Write(
                    LogLevel.Error, nameof(OpenALPlaybackOutput), _instanceId, $"Event=DisposeAudioError Backend={BackendName} Error={ex.Message}", ex);
            }
            finally
            {
                ReleaseNativeResources();
                _state = AudioOutputState.Disposed;
                FluxMediaLog.Write(
                    LogLevel.Information, nameof(OpenALPlaybackOutput), _instanceId, $"Event=DisposeAudio Backend={BackendName} WrittenBytes={Interlocked.Read(ref _writtenBytes)}");
            }
        }
    }

    /// <summary>
    /// 检查资源是否就绪并绑定音频上下文。
    /// </summary>
    private bool PrepareOperation()
    {
        if (_disposed || _source == 0 || _context == IntPtr.Zero) return false;
        EnsureContextCurrent();
        return true;
    }

    /// <summary>
    /// 将当前音频上下文绑定到执行线程。
    /// </summary>
    private void EnsureContextCurrent()
    {
        if (OpenALNative.AlcMakeContextCurrent(_context) == 0)
        {
            throw new InvalidOperationException("OpenAL 无法在当前线程激活音频上下文。");
        }
    }

    /// <summary>
    /// 将当前音量和静音设置应用到设备。
    /// </summary>
    private void ApplyVolume()
    {
        OpenALNative.AlSourcef(_source, AlGain, _muted ? 0f : (float)_volume);
        ThrowIfAlError("alSourcef(AL_GAIN)");
    }

    /// <summary>
    /// 上传 PCM 数据并加入音频播放队列。
    /// </summary>
    private void QueuePcmBuffer(byte[] data, int length)
    {
        OpenALNative.AlGenBuffers(1, out var buffer);
        ThrowIfAlError("alGenBuffers");

        try
        {
            var format = _format!.Channels == 1 ? AlFormatMono16 : AlFormatStereo16;
            OpenALNative.AlBufferData(buffer, format, data, length, _format.SampleRate);
            ThrowIfAlError("alBufferData");
            OpenALNative.AlSourceQueueBuffers(_source, 1, ref buffer);
            ThrowIfAlError("alSourceQueueBuffers");
            _queuedBufferSizes.Enqueue(length);
            Interlocked.Add(ref _queuedBytes, length);
        }
        catch
        {
            if (buffer != 0)
            {
                OpenALNative.AlDeleteBuffers(1, ref buffer);
            }

            throw;
        }
    }

    /// <summary>
    /// 设备可播放且未暂停时启动音频源。
    /// </summary>
    private void EnsurePlaying()
    {
        OpenALNative.AlGetSourcei(_source, AlSourceState, out var sourceState);
        ThrowIfAlError("alGetSourcei(AL_SOURCE_STATE)");
        if (sourceState == AlPlaying || _state == AudioOutputState.Paused) return;
        OpenALNative.AlSourcePlay(_source);
        ThrowIfAlError("alSourcePlay");
        _state = AudioOutputState.Playing;
    }

    /// <summary>
    /// 回收已播放完成的音频缓冲区。
    /// </summary>
    private void ReclaimProcessedBuffers()
    {
        OpenALNative.AlGetSourcei(_source, AlBuffersProcessed, out var processed);
        ThrowIfAlError("alGetSourcei(AL_BUFFERS_PROCESSED)");
        for (var index = 0; index < processed; index++)
        {
            OpenALNative.AlSourceUnqueueBuffers(_source, 1, out var buffer);
            ThrowIfAlError("alSourceUnqueueBuffers");
            if (buffer != 0)
            {
                OpenALNative.AlDeleteBuffers(1, ref buffer);
                ThrowIfAlError("alDeleteBuffers");
            }

            if (_queuedBufferSizes.Count > 0)
            {
                Interlocked.Add(ref _queuedBytes, -_queuedBufferSizes.Dequeue());
            }
        }
    }

    /// <summary>
    /// 停止并回收音频源中的缓冲区。
    /// </summary>
    private void ClearQueuedBuffers()
    {
        OpenALNative.AlGetSourcei(_source, AlBuffersQueued, out var queued);
        ThrowIfAlError("alGetSourcei(AL_BUFFERS_QUEUED)");
        for (var index = 0; index < queued; index++)
        {
            OpenALNative.AlSourceUnqueueBuffers(_source, 1, out var buffer);
            ThrowIfAlError("alSourceUnqueueBuffers");
            if (buffer != 0)
            {
                OpenALNative.AlDeleteBuffers(1, ref buffer);
                ThrowIfAlError("alDeleteBuffers");
            }

            if (_queuedBufferSizes.Count > 0)
            {
                Interlocked.Add(ref _queuedBytes, -_queuedBufferSizes.Dequeue());
            }
        }

        _queuedBufferSizes.Clear();
        Interlocked.Exchange(ref _queuedBytes, 0);
    }

    /// <summary>
    /// 按当前音频格式将字节数换算为时长。
    /// </summary>
    private TimeSpan DurationFromBytes(long bytes)
    {
        var format = _format;
        if (format is null || format.SampleRate <= 0 || format.Channels <= 0 || format.BitsPerSample <= 0) return TimeSpan.Zero;
        var bytesPerSecond = format.SampleRate * format.Channels * (format.BitsPerSample / 8d);
        return bytesPerSecond <= 0 ? TimeSpan.Zero : TimeSpan.FromSeconds(Math.Max(0, bytes / bytesPerSecond));
    }

    /// <summary>
    /// 释放音频缓冲、音频源和设备上下文。
    /// </summary>
    private void ReleaseNativeResources()
    {
        if (_context != IntPtr.Zero)
        {
            try
            {
                OpenALNative.AlcMakeContextCurrent(_context);
                if (_source != 0)
                {
                    OpenALNative.AlDeleteSources(1, ref _source);
                }
            }
            finally
            {
                OpenALNative.AlcMakeContextCurrent(IntPtr.Zero);
                OpenALNative.AlcDestroyContext(_context);
                _context = IntPtr.Zero;
                _source = 0;
            }
        }

        if (_device != IntPtr.Zero)
        {
            OpenALNative.AlcCloseDevice(_device);
            _device = IntPtr.Zero;
        }
    }

    /// <summary>
    /// OpenAL 返回错误时抛出异常。
    /// </summary>
    private void ThrowIfAlError(string operation)
    {
        var error = OpenALNative.AlGetError();
        if (error != AlNoError)
        {
            throw new InvalidOperationException($"OpenAL 调用失败：Operation={operation} ErrorCode=0x{error:X4}");
        }
    }

    /// <summary>
    /// 记录设备错误并更新不可用状态。
    /// </summary>
    private void Fail(string message)
    {
        LastError = message;
        _state = AudioOutputState.Failed;
        FluxMediaLog.Write(
            LogLevel.Error, nameof(OpenALPlaybackOutput), _instanceId, $"Event=AudioOutputInitFailed Backend={BackendName} Error={message}");
    }

    /// <summary>
    /// 按平台调用 OpenAL 原生接口。
    /// </summary>
    private static class OpenALNative
    {
        /// <summary>
        /// 是否 macOS 操作系统。
        /// </summary>
        private static bool IsMacOS => RuntimeInformation.IsOSPlatform(OSPlatform.OSX);

        /// <summary>
        /// 调用 OpenAL 打开音频设备。
        /// </summary>
        internal static IntPtr AlcOpenDevice(IntPtr deviceName) => IsMacOS ? MacOS.AlcOpenDevice(deviceName) : Linux.AlcOpenDevice(deviceName);

        /// <summary>
        /// 为音频设备创建 OpenAL 上下文。
        /// </summary>
        internal static IntPtr AlcCreateContext(IntPtr device, IntPtr attributes) => IsMacOS ? MacOS.AlcCreateContext(device, attributes) : Linux.AlcCreateContext(device, attributes);

        /// <summary>
        /// 将 OpenAL 上下文绑定到当前线程。
        /// </summary>
        internal static byte AlcMakeContextCurrent(IntPtr context) => IsMacOS ? MacOS.AlcMakeContextCurrent(context) : Linux.AlcMakeContextCurrent(context);

        /// <summary>
        /// 销毁 OpenAL 音频上下文。
        /// </summary>
        internal static void AlcDestroyContext(IntPtr context)
        {
            if (IsMacOS)
            {
                MacOS.AlcDestroyContext(context);
            }
            else
            {
                Linux.AlcDestroyContext(context);
            }
        }

        /// <summary>
        /// 关闭 OpenAL 音频设备。
        /// </summary>
        internal static byte AlcCloseDevice(IntPtr device) => IsMacOS ? MacOS.AlcCloseDevice(device) : Linux.AlcCloseDevice(device);

        /// <summary>
        /// 创建 OpenAL 音频源。
        /// </summary>
        internal static void AlGenSources(int count, out uint source)
        {
            if (IsMacOS)
            {
                MacOS.AlGenSources(count, out source);
            }
            else
            {
                Linux.AlGenSources(count, out source);
            }
        }

        /// <summary>
        /// 删除 OpenAL 音频源。
        /// </summary>
        internal static void AlDeleteSources(int count, ref uint source)
        {
            if (IsMacOS)
            {
                MacOS.AlDeleteSources(count, ref source);
            }
            else
            {
                Linux.AlDeleteSources(count, ref source);
            }
        }

        /// <summary>
        /// 启动 OpenAL 音频源播放。
        /// </summary>
        internal static void AlSourcePlay(uint source)
        {
            if (IsMacOS)
            {
                MacOS.AlSourcePlay(source);
            }
            else
            {
                Linux.AlSourcePlay(source);
            }
        }

        /// <summary>
        /// 暂停 OpenAL 音频源。
        /// </summary>
        internal static void AlSourcePause(uint source)
        {
            if (IsMacOS)
            {
                MacOS.AlSourcePause(source);
            }
            else
            {
                Linux.AlSourcePause(source);
            }

        }

        /// <summary>
        /// 停止 OpenAL 音频源。
        /// </summary>
        internal static void AlSourceStop(uint source)
        {
            if (IsMacOS)
            {
                MacOS.AlSourceStop(source);
            }
            else
            {
                Linux.AlSourceStop(source);
            }
        }

        /// <summary>
        /// 设置 OpenAL 音频源的浮点参数。
        /// </summary>
        internal static void AlSourcef(uint source, int parameter, float value)
        {
            if (IsMacOS)
            {
                MacOS.AlSourcef(source, parameter, value);
            }
            else
            {
                Linux.AlSourcef(source, parameter, value);
            }
        }
        /// <summary>
        /// 读取 OpenAL 音频源的整数状态。
        /// </summary>
        internal static void AlGetSourcei(uint source, int parameter, out int value)
        {
            if (IsMacOS)
            {
                MacOS.AlGetSourcei(source, parameter, out value);
            }
            else
            {
                Linux.AlGetSourcei(source, parameter, out value);
            }
        }

        /// <summary>
        /// 创建 OpenAL 音频缓冲区。
        /// </summary>
        internal static void AlGenBuffers(int count, out uint buffer)
        {
            if (IsMacOS)
            {
                MacOS.AlGenBuffers(count, out buffer);
            }
            else
            {
                Linux.AlGenBuffers(count, out buffer);
            }
        }

        /// <summary>
        /// 删除 OpenAL 音频缓冲区。
        /// </summary>
        internal static void AlDeleteBuffers(int count, ref uint buffer)
        {
            if (IsMacOS)
            {
                MacOS.AlDeleteBuffers(count, ref buffer);
            }
            else
            {
                Linux.AlDeleteBuffers(count, ref buffer);
            }
        }

        /// <summary>
        /// 将 PCM 数据上传到 OpenAL 缓冲区。
        /// </summary>
        internal static void AlBufferData(uint buffer, int format, byte[] data, int size, int frequency)
        {
            if (IsMacOS)
            {
                MacOS.AlBufferData(buffer, format, data, size, frequency);
            }
            else
            {
                Linux.AlBufferData(buffer, format, data, size, frequency);
            }
        }

        /// <summary>
        /// 将缓冲区加入 OpenAL 音频源队列。
        /// </summary>
        internal static void AlSourceQueueBuffers(uint source, int count, ref uint buffer)
        {
            if (IsMacOS)
                MacOS.AlSourceQueueBuffers(source, count, ref buffer);
            else
                Linux.AlSourceQueueBuffers(source, count, ref buffer);
        }

        /// <summary>
        /// 从 OpenAL 音频源取出已处理缓冲区。
        /// </summary>
        internal static void AlSourceUnqueueBuffers(uint source, int count, out uint buffer)
        {
            if (IsMacOS)
            {
                MacOS.AlSourceUnqueueBuffers(source, count, out buffer);
            }
            else
            {
                Linux.AlSourceUnqueueBuffers(source, count, out buffer);
            }
        }

        /// <summary>
        /// 读取并清除当前 OpenAL 错误码。
        /// </summary>
        internal static int AlGetError() => IsMacOS ? MacOS.AlGetError() : Linux.AlGetError();

        /// <summary>
        /// Linux 平台的 OpenAL 接口。
        /// </summary>
        private static class Linux
        {
            /// <summary>
            /// 原生库名称。
            /// </summary>
            private const string LibraryName = "libopenal.so.1";

            /// <summary>
            /// 调用 OpenAL 打开音频设备。
            /// </summary>
            [DllImport(LibraryName, EntryPoint = "alcOpenDevice")] internal static extern IntPtr AlcOpenDevice(IntPtr deviceName);

            /// <summary>
            /// 为音频设备创建 OpenAL 上下文。
            /// </summary>
            [DllImport(LibraryName, EntryPoint = "alcCreateContext")] internal static extern IntPtr AlcCreateContext(IntPtr device, IntPtr attributes);

            /// <summary>
            /// 将 OpenAL 上下文绑定到当前线程。
            /// </summary>
            [DllImport(LibraryName, EntryPoint = "alcMakeContextCurrent")] internal static extern byte AlcMakeContextCurrent(IntPtr context);

            /// <summary>
            /// 销毁 OpenAL 音频上下文。
            /// </summary>
            [DllImport(LibraryName, EntryPoint = "alcDestroyContext")] internal static extern void AlcDestroyContext(IntPtr context);

            /// <summary>
            /// 关闭 OpenAL 音频设备。
            /// </summary>
            [DllImport(LibraryName, EntryPoint = "alcCloseDevice")] internal static extern byte AlcCloseDevice(IntPtr device);

            /// <summary>
            /// 创建 OpenAL 音频源。
            /// </summary>
            [DllImport(LibraryName, EntryPoint = "alGenSources")] internal static extern void AlGenSources(int count, out uint source);

            /// <summary>
            /// 删除 OpenAL 音频源。
            /// </summary>
            [DllImport(LibraryName, EntryPoint = "alDeleteSources")] internal static extern void AlDeleteSources(int count, ref uint source);

            /// <summary>
            /// 启动 OpenAL 音频源播放。
            /// </summary>
            [DllImport(LibraryName, EntryPoint = "alSourcePlay")] internal static extern void AlSourcePlay(uint source);

            /// <summary>
            /// 暂停 OpenAL 音频源。
            /// </summary>
            [DllImport(LibraryName, EntryPoint = "alSourcePause")] internal static extern void AlSourcePause(uint source);

            /// <summary>
            /// 停止 OpenAL 音频源。
            /// </summary>
            [DllImport(LibraryName, EntryPoint = "alSourceStop")] internal static extern void AlSourceStop(uint source);

            /// <summary>
            /// 设置 OpenAL 音频源的浮点参数。
            /// </summary>
            [DllImport(LibraryName, EntryPoint = "alSourcef")] internal static extern void AlSourcef(uint source, int parameter, float value);

            /// <summary>
            /// 读取 OpenAL 音频源的整数状态。
            /// </summary>
            [DllImport(LibraryName, EntryPoint = "alGetSourcei")] internal static extern void AlGetSourcei(uint source, int parameter, out int value);

            /// <summary>
            /// 创建 OpenAL 音频缓冲区。
            /// </summary>
            [DllImport(LibraryName, EntryPoint = "alGenBuffers")] internal static extern void AlGenBuffers(int count, out uint buffer);

            /// <summary>
            /// 删除 OpenAL 音频缓冲区。
            /// </summary>
            [DllImport(LibraryName, EntryPoint = "alDeleteBuffers")] internal static extern void AlDeleteBuffers(int count, ref uint buffer);

            /// <summary>
            /// 将 PCM 数据上传到 OpenAL 缓冲区。
            /// </summary>
            [DllImport(LibraryName, EntryPoint = "alBufferData")] internal static extern void AlBufferData(uint buffer, int format, byte[] data, int size, int frequency);

            /// <summary>
            /// 将缓冲区加入 OpenAL 音频源队列。
            /// </summary>
            [DllImport(LibraryName, EntryPoint = "alSourceQueueBuffers")] internal static extern void AlSourceQueueBuffers(uint source, int count, ref uint buffer);

            /// <summary>
            /// 从 OpenAL 音频源取出已处理缓冲区。
            /// </summary>
            [DllImport(LibraryName, EntryPoint = "alSourceUnqueueBuffers")] internal static extern void AlSourceUnqueueBuffers(uint source, int count, out uint buffer);

            /// <summary>
            /// 读取并清除当前 OpenAL 错误码。
            /// </summary>
            [DllImport(LibraryName, EntryPoint = "alGetError")] internal static extern int AlGetError();
        }

        /// <summary>
        /// macOS 平台的 OpenAL 接口。
        /// </summary>
        private static class MacOS
        {
            /// <summary>
            /// 原生库名称。
            /// </summary>
            private const string LibraryName = "/System/Library/Frameworks/OpenAL.framework/OpenAL";

            /// <summary>
            /// 调用 OpenAL 打开音频设备。
            /// </summary>
            [DllImport(LibraryName, EntryPoint = "alcOpenDevice")] internal static extern IntPtr AlcOpenDevice(IntPtr deviceName);

            /// <summary>
            /// 为音频设备创建 OpenAL 上下文。
            /// </summary>
            [DllImport(LibraryName, EntryPoint = "alcCreateContext")] internal static extern IntPtr AlcCreateContext(IntPtr device, IntPtr attributes);

            /// <summary>
            /// 将 OpenAL 上下文绑定到当前线程。
            /// </summary>
            [DllImport(LibraryName, EntryPoint = "alcMakeContextCurrent")] internal static extern byte AlcMakeContextCurrent(IntPtr context);

            /// <summary>
            /// 销毁 OpenAL 音频上下文。
            /// </summary>
            [DllImport(LibraryName, EntryPoint = "alcDestroyContext")] internal static extern void AlcDestroyContext(IntPtr context);

            /// <summary>
            /// 关闭 OpenAL 音频设备。
            /// </summary>
            [DllImport(LibraryName, EntryPoint = "alcCloseDevice")] internal static extern byte AlcCloseDevice(IntPtr device);

            /// <summary>
            /// 创建 OpenAL 音频源。
            /// </summary>
            [DllImport(LibraryName, EntryPoint = "alGenSources")] internal static extern void AlGenSources(int count, out uint source);

            /// <summary>
            /// 删除 OpenAL 音频源。
            /// </summary>
            [DllImport(LibraryName, EntryPoint = "alDeleteSources")] internal static extern void AlDeleteSources(int count, ref uint source);

            /// <summary>
            /// 启动 OpenAL 音频源播放。
            /// </summary>
            [DllImport(LibraryName, EntryPoint = "alSourcePlay")] internal static extern void AlSourcePlay(uint source);

            /// <summary>
            /// 暂停 OpenAL 音频源。
            /// </summary>
            [DllImport(LibraryName, EntryPoint = "alSourcePause")] internal static extern void AlSourcePause(uint source);

            /// <summary>
            /// 停止 OpenAL 音频源。
            /// </summary>
            [DllImport(LibraryName, EntryPoint = "alSourceStop")] internal static extern void AlSourceStop(uint source);

            /// <summary>
            /// 设置 OpenAL 音频源的浮点参数。
            /// </summary>
            [DllImport(LibraryName, EntryPoint = "alSourcef")] internal static extern void AlSourcef(uint source, int parameter, float value);

            /// <summary>
            /// 读取 OpenAL 音频源的整数状态。
            /// </summary>
            [DllImport(LibraryName, EntryPoint = "alGetSourcei")] internal static extern void AlGetSourcei(uint source, int parameter, out int value);

            /// <summary>
            /// 创建 OpenAL 音频缓冲区。
            /// </summary>
            [DllImport(LibraryName, EntryPoint = "alGenBuffers")] internal static extern void AlGenBuffers(int count, out uint buffer);

            /// <summary>
            /// 删除 OpenAL 音频缓冲区。
            /// </summary>
            [DllImport(LibraryName, EntryPoint = "alDeleteBuffers")] internal static extern void AlDeleteBuffers(int count, ref uint buffer);

            /// <summary>
            /// 将 PCM 数据上传到 OpenAL 缓冲区。
            /// </summary>
            [DllImport(LibraryName, EntryPoint = "alBufferData")] internal static extern void AlBufferData(uint buffer, int format, byte[] data, int size, int frequency);

            /// <summary>
            /// 将缓冲区加入 OpenAL 音频源队列。
            /// </summary>
            [DllImport(LibraryName, EntryPoint = "alSourceQueueBuffers")] internal static extern void AlSourceQueueBuffers(uint source, int count, ref uint buffer);

            /// <summary>
            /// 从 OpenAL 音频源取出已处理缓冲区。
            /// </summary>
            [DllImport(LibraryName, EntryPoint = "alSourceUnqueueBuffers")] internal static extern void AlSourceUnqueueBuffers(uint source, int count, out uint buffer);

            /// <summary>
            /// 读取并清除当前 OpenAL 错误码。
            /// </summary>
            [DllImport(LibraryName, EntryPoint = "alGetError")] internal static extern int AlGetError();
        }
    }
}
