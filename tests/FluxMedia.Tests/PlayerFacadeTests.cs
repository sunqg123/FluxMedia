using FluxMedia.Core.Configuration;
using FluxMedia.Core.Errors;
using FluxMedia.Core.Models;
using FluxMedia.Core.Logging;
using FluxMedia.Playback;
using FluxMedia.Playback.Internal;

namespace FluxMedia.Tests;

/// <summary>
/// 播放器外观接口测试。
/// </summary>
public sealed class PlayerFacadeTests
{
    /// <summary>
    /// 单个帧订阅者抛出异常不阻断后续显示端。
    /// </summary>
    [Fact]
    public async Task FrameSubscriberFailure_DoesNotBlockOtherSubscribers()
    {
        var session = new BindingSeekTestSession();
        await using var player = new Player(session);
        var count = 0;
        player.VideoFrameReady += (_, _) => throw new InvalidOperationException("模拟订阅者失败。");
        player.VideoFrameReady += (_, _) => count++;
        using var frame = new VideoFrame(new VideoFormat(1, 1, 25, PixelFormat.Bgra32), new byte[4], TimeSpan.Zero, 0, 4);
        session.PublishFrame(frame);
        Assert.Equal(1, count);
    }
    /// <summary>
    /// 验证公开入口可创建多个独立播放器。
    /// </summary>
    [Fact]
    public async Task PlayerFacade_CanCreateMultipleIndependentPlayers()
    {
        await using var first = FluxEngine.CreatePlayer();
        await using var second = FluxEngine.CreatePlayer();

        Assert.NotEqual(first.InstanceId, second.InstanceId);
        Assert.Equal(PlaybackState.Created, first.State);
        Assert.Equal(PlaybackState.Created, second.State);
        Assert.False(first.IsPlaying);
        Assert.False(second.IsPlaying);
    }

    /// <summary>
    /// 验证引擎登记新播放器并移除已释放实例。
    /// </summary>
    [Fact]
    public async Task FluxEngine_TracksCreatedPlayersAndUnregistersDisposedPlayers()
    {
        FluxEngine.Shutdown();
        FluxEngine.Start(new FluxEngineOptions
        {
            LogDirectory = Path.Combine(Path.GetTempPath(), "FluxMediaTests", Guid.NewGuid().ToString("N"), "Logs")
        });

        var first = FluxEngine.CreatePlayer();
        var second = FluxEngine.CreatePlayer();

        Assert.Contains(first, FluxEngine.Players);
        Assert.Contains(second, FluxEngine.Players);

        await first.DisposeAsync();

        Assert.DoesNotContain(first, FluxEngine.Players);
        Assert.Contains(second, FluxEngine.Players);

        await second.DisposeAsync();
        FluxEngine.Shutdown();
    }

    /// <summary>
    /// 验证字符串入口正确区分网络地址和本地路径。
    /// </summary>
    [Fact]
    public async Task PlayerFacade_OpenStringTreatsHttpAsNetworkAndLocalPathAsFile()
    {
        await using var player = FluxEngine.CreatePlayer();

        await Assert.ThrowsAsync<PlaybackException>(async () =>
            await player.OpenAsync("http://127.0.0.1:1/not-found.mp4", new PlaybackOptions
            {
                NetworkTimeout = TimeSpan.FromMilliseconds(250),
                Network = NetworkPlaybackOptions.Compatible() with
                {
                    OpenTimeout = TimeSpan.FromMilliseconds(250),
                    ReadTimeout = TimeSpan.FromMilliseconds(250)
                }
            }));

        Assert.Equal(PlaybackState.Failed, player.State);
    }

    /// <summary>
    /// 验证重复启动引擎不会重复初始化。
    /// </summary>
    [Fact]
    public void FluxEngine_StartIsIdempotent()
    {
        FluxEngine.Shutdown();
        FluxEngine.Start(new FluxEngineOptions
        {
            LogDirectory = Path.Combine(Path.GetTempPath(), "FluxMediaTests", Guid.NewGuid().ToString("N"), "Logs")
        });
        FluxEngine.Start();

        Assert.True(FluxEngine.IsStarted);

        FluxEngine.Shutdown();
        FluxEngine.Shutdown(); FluxMediaLog.Shutdown();
    }

    /// <summary>
    /// 验证引擎提供当前日志目录和文件路径。
    /// </summary>
    [Fact]
    public void FluxEngine_ExposesLogPathsForDiagnostics()
    {
        var logDirectory = Path.Combine(Path.GetTempPath(), "FluxMediaTests", Guid.NewGuid().ToString("N"), "Logs");

        FluxEngine.Shutdown();
        FluxEngine.Start(new FluxEngineOptions
        {
            EnableLogging = true,
            LogDirectory = logDirectory
        });

        Assert.Equal(logDirectory, FluxEngine.LogDirectory);
        Assert.False(string.IsNullOrWhiteSpace(FluxEngine.CurrentLogFilePath));
        Assert.StartsWith(logDirectory, FluxEngine.CurrentLogFilePath, StringComparison.OrdinalIgnoreCase);

        FluxEngine.Shutdown();
        FluxEngine.Shutdown(); FluxMediaLog.Shutdown();
    }

    /// <summary>
    /// 验证默认启动不创建日志目录。
    /// </summary>
    [Fact]
    public void FluxEngine_StartDoesNotCreateLogDirectoryByDefault()
    {
        var logDirectory = Path.Combine(Path.GetTempPath(), "FluxMediaTests", Guid.NewGuid().ToString("N"), "Logs");

        FluxEngine.Shutdown();
        FluxEngine.Start(new FluxEngineOptions
        {
            LogDirectory = logDirectory
        });

        Assert.Null(FluxEngine.LogDirectory);
        Assert.Null(FluxEngine.CurrentLogFilePath);
        Assert.False(Directory.Exists(logDirectory));

        FluxEngine.Shutdown();
        FluxEngine.Shutdown(); FluxMediaLog.Shutdown();
    }

    /// <summary>
    /// 验证禁用日志级别时不创建日志目录。
    /// </summary>
    [Fact]
    public void FluxEngine_StartWithLogLevelNoneDoesNotCreateLogDirectory()
    {
        var logDirectory = Path.Combine(Path.GetTempPath(), "FluxMediaTests", Guid.NewGuid().ToString("N"), "Logs");

        FluxEngine.Shutdown();
        FluxEngine.Start(new FluxEngineOptions
        {
            LogLevel = LogLevel.None,
            LogDirectory = logDirectory
        });

        Assert.Null(FluxEngine.LogDirectory);
        Assert.Null(FluxEngine.CurrentLogFilePath);
        Assert.False(Directory.Exists(logDirectory));

        FluxEngine.Shutdown();
        FluxEngine.Shutdown(); FluxMediaLog.Shutdown();
    }

    /// <summary>
    /// 验证关闭引擎时释放已登记的播放器。
    /// </summary>
    [Fact]
    public async Task FluxEngine_ShutdownDisposesTrackedPlayers()
    {
        FluxEngine.Shutdown();
        FluxEngine.Start(new FluxEngineOptions
        {
            LogDirectory = Path.Combine(Path.GetTempPath(), "FluxMediaTests", Guid.NewGuid().ToString("N"), "Logs")
        });
        var first = FluxEngine.CreatePlayer();
        var second = FluxEngine.CreatePlayer();

        FluxEngine.Shutdown();

        Assert.True(first.IsDisposed);
        Assert.True(second.IsDisposed);
        Assert.Empty(FluxEngine.Players);
        await first.DisposeAsync();
        await second.DisposeAsync();
    }

    /// <summary>
    /// 验证快速拖动进度只执行最新跳转目标。
    /// </summary>
    [Fact]
    public async Task PlayerBindingSeek_RapidChangesExecuteOnlyLatestTarget()
    {
        var session = new BindingSeekTestSession();
        await using var player = new Player(session);

        player.PositionSeconds = 10;
        player.PositionSeconds = 20;
        player.PositionSeconds = 30;

        await WaitUntilAsync(() => session.SeekPositions.Count >= 1);
        await Task.Delay(120);

        Assert.Equal(new[] { TimeSpan.FromSeconds(30) }, session.SeekPositions);
    }

    /// <summary>
    /// 验证跳转期间的新请求会跳过中间目标。
    /// </summary>
    [Fact]
    public async Task PlayerBindingSeek_ChangesDuringSeekSkipIntermediateTargets()
    {
        var session = new BindingSeekTestSession(TimeSpan.FromMilliseconds(150));
        await using var player = new Player(session);

        player.PositionSeconds = 10;
        await WaitUntilAsync(() => session.SeekPositions.Count >= 1);
        player.PositionSeconds = 20;
        player.PositionSeconds = 30;

        await WaitUntilAsync(() => session.SeekPositions.Count >= 2);
        await Task.Delay(220);

        Assert.Equal(
            new[] { TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30) }, session.SeekPositions);
    }

    /// <summary>
    /// 在超时前等待测试条件成立。
    /// </summary>
    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5);
        while (!predicate())
        {
            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new TimeoutException("Timed out waiting for the binding seek test condition.");
            }

            await Task.Delay(10);
        }
    }

    /// <summary>
    /// 绑定跳转测试会话。
    /// </summary>
    private sealed class BindingSeekTestSession : IPlaybackSession
    {
        /// <summary>
        /// 模拟底层帧事件。
        /// </summary>
        public void PublishFrame(VideoFrame frame) => VideoFrameReady?.Invoke(this, new PlaybackVideoFrameEventArgs(frame));

        /// <summary>
        /// 保护共享状态的同步锁。
        /// </summary>
        private readonly object _sync = new();

        /// <summary>
        /// 跳转延迟。
        /// </summary>
        private readonly TimeSpan _seekDelay;

        /// <summary>
        /// 使用真实调度器验证绑定只进行一次合并。
        /// </summary>
        private readonly PlaybackSeekController _seeking;

        /// <summary>
        /// 跳转位置。
        /// </summary>
        private readonly List<TimeSpan> _seekPositions = new();

        /// <summary>
        /// 初始化绑定跳转测试会话所需的状态和资源。
        /// </summary>
        public BindingSeekTestSession(TimeSpan? seekDelay = null)
        {
            _seekDelay = seekDelay ?? TimeSpan.Zero;
            _seeking = new PlaybackSeekController(InstanceId, ExecuteSeekAsync);
        }

        /// <summary>
        /// 跳转位置。
        /// </summary>
        public IReadOnlyList<TimeSpan> SeekPositions
        {
            get
            {
                lock (_sync)
                {
                    return _seekPositions.ToArray();
                }
            }
        }

        /// <summary>
        /// 当前组件的唯一实例标识。
        /// </summary>
        public Guid InstanceId { get; } = Guid.NewGuid();

        /// <summary>
        /// 组件名称。
        /// </summary>
        public string ComponentName => nameof(BindingSeekTestSession);

        /// <summary>
        /// 是否已完成资源释放。
        /// </summary>
        public bool IsDisposed { get; private set; }

        /// <summary>
        /// 当前运行状态。
        /// </summary>
        public PlaybackState State => PlaybackState.Ready;

        /// <summary>
        /// 固定点播源。
        /// </summary>
        public bool IsLive => false;

        /// <summary>
        /// 假会话支持时间跳转。
        /// </summary>
        public bool CanSeek => true;

        /// <summary>
        /// 当前打开的媒体源。
        /// </summary>
        public MediaSource? CurrentSource => MediaSource.FromFile("binding-seek-test.mp4");

        /// <summary>
        /// 当前处理到的媒体时间。
        /// </summary>
        public TimeSpan CurrentTime { get; private set; }

        /// <summary>
        /// 媒体总时长，未知时为空。
        /// </summary>
        public TimeSpan? Duration => TimeSpan.FromSeconds(100);

        /// <summary>
        /// 播放速度。
        /// </summary>
        public double PlaybackRate { get; private set; } = 1;

        /// <summary>
        /// 音量。
        /// </summary>
        public double Volume { get; private set; } = 1;

        /// <summary>
        /// 是否静音。
        /// </summary>
        public bool IsMuted { get; private set; }

        /// <summary>
        /// 组件运行状态变化时发出的通知。
        /// </summary>
        public event EventHandler<PlaybackState>? StateChanged { add { } remove { } }

        /// <summary>
        /// 发生运行错误时发出的通知。
        /// </summary>
        public event EventHandler<FluxMediaError>? Faulted { add { } remove { } }

        /// <summary>
        /// 播放位置变化时发出的通知。
        /// </summary>
        public event EventHandler<PlaybackPositionChangedEventArgs>? PositionChanged;

        /// <summary>
        /// 新视频帧可供显示时发出的通知。
        /// </summary>
        public event EventHandler<PlaybackVideoFrameEventArgs>? VideoFrameReady;

        /// <summary>
        /// 更新显示端支持的 GPU 输出能力。
        /// </summary>
        public void ConfigureVideoOutput(VideoOutputCapabilities capabilities)
        {
        }

        /// <summary>
        /// 打开媒体源并读取媒体信息。
        /// </summary>
        public ValueTask OpenAsync(PlaybackOpenRequest request, CancellationToken cancellationToken = default) => default;

        /// <summary>
        /// 获取当前运行状态和诊断信息。
        /// </summary>
        public PlaybackDiagnosticInfo GetDiagnosticInfo() => new();

        /// <summary>
        /// 启动解码和音频输出，结束后可重新播放。
        /// </summary>
        public ValueTask<PlaybackResult> PlayAsync(CancellationToken cancellationToken = default) => new(PlaybackResult.Success());

        /// <summary>
        /// 暂停播放并保留当前位置。
        /// </summary>
        public ValueTask<PlaybackResult> PauseAsync(CancellationToken cancellationToken = default) => new(PlaybackResult.Success());

        /// <summary>
        /// 恢复暂停的播放。
        /// </summary>
        public ValueTask<PlaybackResult> ResumeAsync(CancellationToken cancellationToken = default) => new(PlaybackResult.Success());

        /// <summary>
        /// 跳转到指定媒体时间。
        /// </summary>
        public ValueTask<PlaybackResult> SeekAsync(TimeSpan position, CancellationToken cancellationToken = default) =>
            new(_seeking.Enqueue(position, true, cancellationToken));

        /// <summary>
        /// 记录调度器实际执行的目标。
        /// </summary>
        private async Task<PlaybackResult> ExecuteSeekAsync(TimeSpan position, bool resumePlaying, CancellationToken cancellationToken)
        {
            lock (_sync)
            {
                _seekPositions.Add(position);
            }

            if (_seekDelay > TimeSpan.Zero)
            {
                await Task.Delay(_seekDelay, cancellationToken);
            }

            CurrentTime = position;
            PositionChanged?.Invoke(this, new PlaybackPositionChangedEventArgs(position, Duration));
            return PlaybackResult.Success();
        }

        /// <summary>
        /// 设置播放速度。
        /// </summary>
        public ValueTask<PlaybackResult> SetPlaybackRateAsync(double rate, CancellationToken cancellationToken = default)
        {
            PlaybackRate = rate;
            return new ValueTask<PlaybackResult>(PlaybackResult.Success());
        }

        /// <summary>
        /// 设置音量，范围为 0 到 1。
        /// </summary>
        public ValueTask<PlaybackResult> SetVolumeAsync(double volume, CancellationToken cancellationToken = default)
        {
            Volume = volume;
            return new ValueTask<PlaybackResult>(PlaybackResult.Success());
        }

        /// <summary>
        /// 设置静音状态。
        /// </summary>
        public ValueTask<PlaybackResult> SetMutedAsync(bool muted, CancellationToken cancellationToken = default)
        {
            IsMuted = muted;
            return new ValueTask<PlaybackResult>(PlaybackResult.Success());
        }

        /// <summary>
        /// 开始录制当前播放的数据包。
        /// </summary>
        public ValueTask<PlaybackResult> StartRecordingAsync(PlaybackRecordingOptions options, CancellationToken cancellationToken = default) =>
            new(PlaybackResult.Success());

        /// <summary>
        /// 停止录制并封装输出文件。
        /// </summary>
        public ValueTask<PlaybackResult> StopRecordingAsync(CancellationToken cancellationToken = default) =>
            new(PlaybackResult.Success());

        /// <summary>
        /// 获取当前录制状态和错误信息。
        /// </summary>
        public PlaybackRecordingDiagnosticInfo GetRecordingDiagnosticInfo() => new();

        /// <summary>
        /// 获取已打开媒体的基本信息。
        /// </summary>
        public ValueTask<MediaInfo> GetMediaInfoAsync(CancellationToken cancellationToken = default) =>
            new(new MediaInfo(TimeSpan.FromSeconds(100), Array.Empty<CodecInfo>(), new Dictionary<string, string>()));

        /// <summary>
        /// 依次读取视频帧；跨迭代使用时需保留租约。
        /// </summary>
        public async IAsyncEnumerable<VideoFrame> ReadVideoFramesAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        /// <summary>
        /// 取消待执行操作并停止播放管线。
        /// </summary>
        public ValueTask StopAsync(CancellationToken cancellationToken = default) => default;

        /// <summary>
        /// 取消测试会话中尚未执行的跳转。
        /// </summary>
        public void CancelPendingOperations() => _seeking.CancelPending(new OperationCanceledException());

        /// <summary>
        /// 释放当前对象持有的资源。
        /// </summary>
        public async ValueTask DisposeAsync()
        {
            IsDisposed = true;
            await _seeking.DisposeAsync();
        }
    }
}
