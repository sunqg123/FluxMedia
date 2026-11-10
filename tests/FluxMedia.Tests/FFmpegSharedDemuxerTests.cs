using System.Diagnostics;
using FluxMedia.Playback.Internal.Audio;
using FluxMedia.Core.Configuration;
using FluxMedia.Core.Logging;
using FluxMedia.Core.Models;
using FluxMedia.FFmpeg.Pipeline;
using FluxMedia.Playback;
using FluxMedia.Playback.Internal;

namespace FluxMedia.Tests;

/// <summary>
/// FFmpeg 共享解复用器测试。
/// </summary>
public sealed class FFmpegSharedDemuxerTests
{
    /// <summary>
    /// 验证一次媒体读取能够分发视频数据包。
    /// </summary>
    [FFmpegFact]
    public async Task SharedDemuxer_RoutesVideoPacketsFromSingleInput()
    {
        var ffmpegRoot = FFmpegFactAttribute.NativeRoot;
        var ffmpegExe = Path.Combine(ffmpegRoot, FFmpegLibraries.FFmpegExecutableName);
        if (!File.Exists(ffmpegExe))
        {
            return;
        }

        var tempDir = Path.Combine(Path.GetTempPath(), "FluxMediaTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var mediaPath = Path.Combine(tempDir, "shared-demux.mp4");

        try
        {
            await RunProcessAsync(
                ffmpegExe, $"-hide_banner -loglevel error -f lavfi -i testsrc=size=64x64:rate=5:duration=0.4 -an -pix_fmt yuv420p -y \"{mediaPath}\"");

            using var demuxer = new FFmpegSharedDemuxer(
                MediaSource.FromFile(mediaPath), new PlaybackOptions
                {
                    NativeLibraries = new NativeLibraryOptions
                    {
                        FFmpegRootDirectory = ffmpegRoot
                    }
                }, Guid.NewGuid(), TimeSpan.Zero);

            await demuxer.StartAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

            Assert.NotNull(demuxer.VideoStream);
            Assert.True(demuxer.ReadPacketCount > 0);
            Assert.True(demuxer.RoutedVideoPacketCount > 0);
            Assert.Equal(0, demuxer.RoutedAudioPacketCount);
        }
        finally
        {
            try
            {
                Directory.Delete(tempDir, recursive: true);
            }
            catch
            {
            }
        }
    }

    /// <summary>
    /// 验证共享解复用器输出的包可以解码为视频帧。
    /// </summary>
    [FFmpegFact]
    public async Task PacketVideoWorker_DecodesFramesFromSharedDemuxerPackets()
    {
        var ffmpegRoot = FFmpegFactAttribute.NativeRoot;
        var ffmpegExe = Path.Combine(ffmpegRoot, FFmpegLibraries.FFmpegExecutableName);
        if (!File.Exists(ffmpegExe))
        {
            return;
        }

        var tempDir = Path.Combine(Path.GetTempPath(), "FluxMediaTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var mediaPath = Path.Combine(tempDir, "packet-video-worker.mp4");
        var frames = new List<VideoFrame>();

        try
        {
            await RunProcessAsync(
                ffmpegExe, $"-hide_banner -loglevel error -f lavfi -i testsrc=size=64x64:rate=10:duration=1 -an -c:v libx264 -bf 2 -g 10 -pix_fmt yuv420p -y \"{mediaPath}\"");

            using var demuxer = new FFmpegSharedDemuxer(
                MediaSource.FromFile(mediaPath), new PlaybackOptions
                {
                    NativeLibraries = new NativeLibraryOptions
                    {
                        FFmpegRootDirectory = ffmpegRoot
                    }
                }, Guid.NewGuid(), TimeSpan.Zero, videoQueueCapacity: 64);

            await demuxer.StartAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.NotNull(demuxer.VideoStream);

            using var worker = new FFmpegPacketVideoPlaybackWorker(
                MediaSource.FromFile(mediaPath), new PlaybackOptions(), Guid.NewGuid(), TimeSpan.Zero, demuxer.VideoStream!, demuxer.VideoPackets, () => demuxer.PacketGeneration, frame => frames.Add(frame), () => false, () => 1.0);

            await worker.RunAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

            Assert.NotEmpty(frames);
            Assert.Equal(10, frames.Count);
            Assert.Equal(TimeSpan.FromMilliseconds(900), frames[^1].Timestamp);
            Assert.All(frames, frame =>
            {
                Assert.Equal(PixelFormat.Bgra32, frame.Format.PixelFormat);
                Assert.Equal(64, frame.Format.Width);
                Assert.Equal(64, frame.Format.Height);
                Assert.True(frame.Buffer.Length >= frame.Stride * frame.Format.Height);
            });
        }
        finally
        {
            foreach (var frame in frames)
            {
                frame.Dispose();
            }

            try
            {
                Directory.Delete(tempDir, recursive: true);
            }
            catch
            {
            }
        }
    }

    /// <summary>
    /// 运行测试辅助进程并检查退出结果。
    /// </summary>
    private static async Task RunProcessAsync(string fileName, string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        }) ?? throw new InvalidOperationException($"Failed to start {fileName}.");

        var stderr = await process.StandardError.ReadToEndAsync();
        var stdout = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"Process failed with exit code {process.ExitCode}.\n{stdout}\n{stderr}");
        }
    }

    /// <summary>
    /// 显示回调尚未返回时不能提前通知完成，避免尾帧混入下一轮。
    /// </summary>
    [FFmpegFact]
    public async Task PlaybackCompleted_WaitsForFrameCallbacksAndFinalTimestamp()
    {
        var mediaPath = Path.Combine(Path.GetTempPath(), $"FluxMediaTail-{Guid.NewGuid():N}.mp4");
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var player = FluxEngine.CreatePlayer(new PlaybackOptions
        {
            NativeLibraries = new NativeLibraryOptions { FFmpegRootDirectory = FFmpegFactAttribute.NativeRoot },
            VideoAcceleration = VideoAccelerationMode.Disabled
        });
        try
        {
            await RunProcessAsync(Path.Combine(FFmpegFactAttribute.NativeRoot, FFmpegLibraries.FFmpegExecutableName),
                $"-hide_banner -loglevel error -f lavfi -i testsrc2=size=64x64:rate=10:duration=1 -an -c:v libx264 -bf 2 -y \"{mediaPath}\"");
            player.VideoFrameReady += (_, _) =>
            {
                entered.TrySetResult();
                release.Wait(TimeSpan.FromSeconds(10));
            };
            player.StateChanged += (_, state) => { if (state == PlaybackState.Completed) completed.TrySetResult(); };
            Assert.True((await player.OpenAndPlayAsync(mediaPath)).IsSuccess);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Delay(1500);
            Assert.False(completed.Task.IsCompleted);
            release.Set();
            await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(TimeSpan.FromMilliseconds(900), player.Position);
        }
        finally
        {
            release.Set();
            await player.StopAsync();
            File.Delete(mediaPath);
        }
    }

    /// <summary>
    /// 音频输出完整 PCM 后等待尾音播放完，取消时能立即退出。
    /// </summary>
    [FFmpegFact]
    public async Task PacketAudioWorker_DrainsPcmAndAllowsCancelingBufferedTail()
    {
        var ffmpegExe = Path.Combine(FFmpegFactAttribute.NativeRoot, FFmpegLibraries.FFmpegExecutableName);
        var mediaPath = Path.Combine(Path.GetTempPath(), $"FluxMediaAudio-{Guid.NewGuid():N}.aac");
        var pcmPath = mediaPath + ".pcm";
        try
        {
            await RunProcessAsync(ffmpegExe, $"-hide_banner -loglevel error -f lavfi -i sine=frequency=1000:sample_rate=48000:duration=0.3 -c:a aac -f adts -y \"{mediaPath}\"");
            await RunProcessAsync(ffmpegExe, $"-hide_banner -loglevel error -i \"{mediaPath}\" -f s16le -y \"{pcmPath}\"");
            var expected = await File.ReadAllBytesAsync(pcmPath);
            foreach (var cancelTail in new[] { false, true })
            {
                using var demuxer = new FFmpegSharedDemuxer(MediaSource.FromFile(mediaPath), new PlaybackOptions
                {
                    NativeLibraries = new NativeLibraryOptions { FFmpegRootDirectory = FFmpegFactAttribute.NativeRoot }
                }, Guid.NewGuid(), TimeSpan.Zero);
                await demuxer.StartAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
                using var output = new BufferedTestOutput(cancelTail);
                using var cancellation = new CancellationTokenSource();
                using var worker = new FFmpegPacketAudioPlaybackWorker(
                    MediaSource.FromFile(mediaPath), Guid.NewGuid(), TimeSpan.Zero, demuxer.AudioStream!, demuxer.AudioPackets, () => demuxer.PacketGeneration,
                    output, () => false, () => 1, () => 1, () => false);
                var task = worker.RunAsync(cancellation.Token);
                await output.TailWaitStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.False(task.IsCompleted);
                if (cancelTail) cancellation.Cancel();
                await task.WaitAsync(TimeSpan.FromSeconds(2));

                Assert.Equal(expected, output.Pcm.ToArray());
                Assert.Equal(cancelTail, output.StoppedWithPendingAudio);
            }
        }
        finally
        {
            File.Delete(mediaPath);
            File.Delete(pcmPath);
        }
    }

    /// <summary>
    /// 捕获 PCM，并模拟设备仍在播放末尾缓冲。
    /// </summary>
    private sealed class BufferedTestOutput : IAudioOutputDevice
    {
        /// <summary>
        /// 是否保持缓冲直到测试取消。
        /// </summary>
        private readonly bool _holdTail;
        /// <summary>
        /// 首次等待设备缓冲的时间。
        /// </summary>
        private DateTimeOffset? _waitStartedAt;
        /// <summary>
        /// 已输出的 PCM 数据。
        /// </summary>
        public MemoryStream Pcm { get; } = new();
        /// <summary>
        /// 工作线程进入尾音等待的信号。
        /// </summary>
        public TaskCompletionSource TailWaitStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        /// <summary>
        /// 停止时是否仍有未播放的音频。
        /// </summary>
        public bool StoppedWithPendingAudio { get; private set; }
        /// <summary>
        /// 模拟设备始终可用。
        /// </summary>
        public bool IsAvailable => true;
        /// <summary>
        /// 测试后端名称。
        /// </summary>
        public string BackendName => "BufferedTest";
        /// <summary>
        /// 测试后端不产生设备错误。
        /// </summary>
        public string? LastError => null;
        /// <summary>
        /// 当前模拟设备状态。
        /// </summary>
        public AudioOutputState State { get; private set; }

        /// <summary>
        /// 设置尾音缓冲的结束方式。
        /// </summary>
        public BufferedTestOutput(bool holdTail) => _holdTail = holdTail;
        /// <summary>
        /// 返回不推进时钟的设备快照。
        /// </summary>
        public AudioOutputDiagnosticInfo GetDiagnosticInfo() => new() { State = State };
        /// <summary>
        /// 记录初始化状态。
        /// </summary>
        public void Initialize(int sampleRate, int channels) => State = AudioOutputState.Initialized;
        /// <summary>
        /// 记录播放状态。
        /// </summary>
        public void Play() => State = AudioOutputState.Playing;
        /// <summary>
        /// 记录暂停状态。
        /// </summary>
        public void Pause() => State = AudioOutputState.Paused;
        /// <summary>
        /// 检查停止是否截断尾音。
        /// </summary>
        public void Stop()
        {
            StoppedWithPendingAudio = GetBufferedDuration() > TimeSpan.Zero;
            State = AudioOutputState.Stopped;
        }
        /// <summary>
        /// 清空已捕获的 PCM。
        /// </summary>
        public void Clear() => Pcm.SetLength(0);
        /// <summary>
        /// 音量不影响测试采样。
        /// </summary>
        public void SetVolume(double volume, bool muted) { }
        /// <summary>
        /// 模拟 150 毫秒尾音，或等待外部取消。
        /// </summary>
        public TimeSpan GetBufferedDuration()
        {
            _waitStartedAt ??= DateTimeOffset.UtcNow;
            TailWaitStarted.TrySetResult();
            return _holdTail || DateTimeOffset.UtcNow - _waitStartedAt < TimeSpan.FromMilliseconds(150)
                ? TimeSpan.FromMilliseconds(150) : TimeSpan.Zero;
        }
        /// <summary>
        /// 测试不引入设备延迟。
        /// </summary>
        public TimeSpan GetDeviceDelay() => TimeSpan.Zero;
        /// <summary>
        /// 保存实际解码产生的 PCM。
        /// </summary>
        public void Write(ReadOnlySpan<byte> pcm) => Pcm.Write(pcm);
        /// <summary>
        /// 释放采样存储。
        /// </summary>
        public void Dispose() => Pcm.Dispose();
    }
}
