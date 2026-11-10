using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using FluxMedia.Core.Configuration;
using FluxMedia.Core.Models;
using FluxMedia.Playback;

namespace FluxMedia.Tests;

/// <summary>
/// 使用本机直播流验证断线、重试和取消，不依赖外部摄像头。
/// </summary>
public sealed class NetworkRecoveryTests
{
    /// <summary>
    /// 一次会话重开失败后，后续恢复继续写入同一个录制文件。
    /// </summary>
    [FFmpegFact]
    public async Task Recording_SurvivesFailedReopenAndContinuesAfterRecovery()
    {
        await using var server = await LiveServer.CreateAsync();
        await using var player = FluxEngine.CreatePlayer(CreateOptions() with
        {
            Network = CreateOptions().Network with { ReconnectDelayMax = 1 }
        });
        var path = Path.Combine(Path.GetTempPath(), $"FluxMediaRecovery-{Guid.NewGuid():N}.mkv");
        try
        {
            var frames = 0;
            player.VideoFrameReady += (_, _) => Interlocked.Increment(ref frames);
            Assert.True((await player.OpenAndPlayAsync(server.Url)).IsSuccess);
            await WaitUntilAsync(() => Volatile.Read(ref frames) > 0);
            Assert.True((await player.StartRecordingAsync(path)).IsSuccess);
            await WaitUntilAsync(() => player.Recording.EncodedFrameCount >= 3);
            var before = player.Recording.EncodedFrameCount;
            server.RejectReconnects = true;
            server.DisconnectFirst();
            await WaitUntilAsync(() => server.Connections >= 3 && player.GetDiagnosticInfo().Network.RecoveryAttemptCount >= 1);
            server.RejectReconnects = false;
            await WaitUntilAsync(() => player.GetDiagnosticInfo().Network.RecoveryAttemptCount >= 2 && player.Recording.EncodedFrameCount >= before + 5);
            Assert.True((await player.StopRecordingAsync()).IsSuccess);
            Assert.True(player.Recording.OutputFileFinalized);
            Assert.Equal(path, player.Recording.OutputPath);
            await player.StopAsync();
            var start = new ProcessStartInfo(Path.Combine(FFmpegFactAttribute.NativeRoot, FFmpegLibraries.FFmpegExecutableName))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true
            };
            foreach (var argument in new[] { "-v", "error", "-xerror", "-i", path, "-f", "null", "-" }) start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!;
            var error = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(process.ExitCode == 0, error);
        }
        finally
        {
            await player.StopAsync();
            File.Delete(path);
        }
    }

    /// <summary>
    /// 停止能中断正在等待服务器响应的打开操作。
    /// </summary>
    [FFmpegFact]
    public async Task StopDuringOpen_CancelsWithoutWaitingForOpenTimeout()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        await using var player = FluxEngine.CreatePlayer(CreateOptions() with
        {
            Network = CreateOptions().Network with { OpenTimeout = TimeSpan.FromSeconds(30) }
        });
        var opening = player.OpenAndPlayAsync($"http://127.0.0.1:{port}/waiting.ts").AsTask();
        using var client = await listener.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await player.StopAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => opening);
        Assert.Equal(PlaybackState.Stopped, player.State);
    }

    /// <summary>
    /// 直播返回 EOF 后自动重连并恢复视频输出。
    /// </summary>
    [FFmpegFact]
    public async Task LiveEof_ReconnectsWithoutReportingCompleted()
    {
        await using var server = await LiveServer.CreateAsync();
        await using var player = FluxEngine.CreatePlayer(CreateOptions());
        var frames = 0;
        var completed = 0;
        player.VideoFrameReady += (_, _) => Interlocked.Increment(ref frames);
        player.StateChanged += (_, state) => { if (state == PlaybackState.Completed) Interlocked.Increment(ref completed); };
        Assert.True((await player.OpenAndPlayAsync(MediaSource.FromNetwork(server.Url))).IsSuccess);
        await WaitUntilAsync(() => Volatile.Read(ref frames) > 0);
        server.DisconnectFirst();

        await WaitUntilAsync(() => player.GetDiagnosticInfo().Network.RecoveryAttemptCount == 1 && server.Connections >= 3);
        var before = Volatile.Read(ref frames);
        await WaitUntilAsync(() => Volatile.Read(ref frames) >= before + 5);
        Assert.Equal(PlaybackState.Playing, player.State);
        Assert.Equal(0, completed);
    }

    /// <summary>
    /// 连续打开失败时用完配置的次数后明确失败。
    /// </summary>
    [FFmpegFact]
    public async Task FailedReopen_UsesAllAttemptsThenFails()
    {
        await using var server = await LiveServer.CreateAsync();
        await using var player = FluxEngine.CreatePlayer(CreateOptions());
        var frames = 0;
        player.VideoFrameReady += (_, _) => Interlocked.Increment(ref frames);
        Assert.True((await player.OpenAndPlayAsync(MediaSource.FromNetwork(server.Url))).IsSuccess);
        await WaitUntilAsync(() => Volatile.Read(ref frames) > 0);
        server.RejectReconnects = true;
        server.DisconnectFirst();

        await WaitUntilAsync(() => player.State == PlaybackState.Failed);
        Assert.Equal(3, player.GetDiagnosticInfo().Network.RecoveryAttemptCount);
        // HTTP 协议层可能先自行重连一次；会话层必须严格限制为三次。
        Assert.InRange(server.Connections, 4, 5);
    }

    /// <summary>
    /// 停止操作取消重试，停止完成后不再创建连接。
    /// </summary>
    [FFmpegFact]
    public async Task StopDuringRetry_CancelsFurtherConnections()
    {
        await using var server = await LiveServer.CreateAsync();
        await using var player = FluxEngine.CreatePlayer(CreateOptions() with
        {
            Network = CreateOptions().Network with { ReconnectDelayMax = 1 }
        });
        var frames = 0;
        player.VideoFrameReady += (_, _) => Interlocked.Increment(ref frames);
        Assert.True((await player.OpenAndPlayAsync(MediaSource.FromNetwork(server.Url))).IsSuccess);
        await WaitUntilAsync(() => Volatile.Read(ref frames) > 0);
        server.RejectReconnects = true;
        server.DisconnectFirst();
        await WaitUntilAsync(() => player.GetDiagnosticInfo().Network.RecoveryAttemptCount > 0);

        await player.StopAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        var connections = server.Connections;
        await Task.Delay(1200);
        Assert.Equal(PlaybackState.Stopped, player.State);
        Assert.Equal(connections, server.Connections);
    }

    /// <summary>
    /// 禁用重连时断线直接失败，不误报自然完成。
    /// </summary>
    [FFmpegFact]
    public async Task ReconnectDisabled_DoesNotCreateAnotherConnection()
    {
        await using var server = await LiveServer.CreateAsync();
        await using var player = FluxEngine.CreatePlayer(CreateOptions() with
        {
            Network = CreateOptions().Network with { ReconnectEnabled = false }
        });
        var frames = 0;
        player.VideoFrameReady += (_, _) => Interlocked.Increment(ref frames);
        Assert.True((await player.OpenAndPlayAsync(MediaSource.FromNetwork(server.Url))).IsSuccess);
        await WaitUntilAsync(() => Volatile.Read(ref frames) > 0);
        server.DisconnectFirst();
        await WaitUntilAsync(() => player.State == PlaybackState.Failed);

        Assert.Equal(0, player.GetDiagnosticInfo().Network.RecoveryAttemptCount);
        Assert.Equal(1, server.Connections);
    }

    /// <summary>
    /// 暂停期间重连后仍保持暂停。
    /// </summary>
    [FFmpegFact]
    public async Task PausedPlayback_ReconnectsWithoutAutoPlaying()
    {
        await using var server = await LiveServer.CreateAsync();
        await using var player = FluxEngine.CreatePlayer(CreateOptions());
        var frames = 0;
        player.VideoFrameReady += (_, _) => Interlocked.Increment(ref frames);
        Assert.True((await player.OpenAndPlayAsync(MediaSource.FromNetwork(server.Url))).IsSuccess);
        await WaitUntilAsync(() => Volatile.Read(ref frames) > 0);
        await player.PauseAsync();
        server.DisconnectFirst();
        await WaitUntilAsync(() => server.Connections >= 3 && player.GetDiagnosticInfo().Network.RecoveryAttemptCount == 1);
        await Task.Delay(300);

        Assert.Equal(PlaybackState.Paused, player.State);
        Assert.True((await player.PlayAsync()).IsSuccess);
        var before = Volatile.Read(ref frames);
        await WaitUntilAsync(() => Volatile.Read(ref frames) > before + 3);
    }

    /// <summary>
    /// 创建短超时的软件解码配置。
    /// </summary>
    private static PlaybackOptions CreateOptions() => new()
    {
        NativeLibraries = new NativeLibraryOptions { FFmpegRootDirectory = FFmpegFactAttribute.NativeRoot },
        VideoAcceleration = VideoAccelerationMode.Disabled,
        NetworkReconnectAttempts = 3,
        Network = new NetworkPlaybackOptions
        {
            OpenTimeout = TimeSpan.FromSeconds(2),
            ReadTimeout = TimeSpan.FromSeconds(2),
            ReconnectDelayMax = 0
        }
    };

    /// <summary>
    /// 有界等待异步状态，超时提供明确断言。
    /// </summary>
    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var clock = Stopwatch.StartNew();
        while (!condition() && clock.Elapsed < TimeSpan.FromSeconds(15)) await Task.Delay(20);
        Assert.True(condition(), "等待播放状态超时。");
    }

    /// <summary>
    /// 持续发送 MPEG-TS，并允许主动结束首个连接。
    /// </summary>
    private sealed class LiveServer : IAsyncDisposable
    {
        /// <summary>
        /// 本机监听端口。
        /// </summary>
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        /// <summary>
        /// 服务器关闭信号。
        /// </summary>
        private readonly CancellationTokenSource _stop = new();
        /// <summary>
        /// 每轮发送的编码视频。
        /// </summary>
        private readonly byte[] _media;
        /// <summary>
        /// 接收连接的后台任务。
        /// </summary>
        private readonly Task _acceptTask;
        /// <summary>
        /// 客户端处理任务，关闭时统一等待。
        /// </summary>
        private readonly List<Task> _clients = new();
        /// <summary>
        /// 请求在完整媒体分段边界结束首次连接。
        /// </summary>
        private int _disconnectRequested;
        /// <summary>
        /// 已接受的连接数。
        /// </summary>
        private int _connections;
        /// <summary>
        /// 是否拒绝后续连接。
        /// </summary>
        private int _rejectReconnects;

        /// <summary>
        /// 启动携带固定素材的本机服务器。
        /// </summary>
        private LiveServer(byte[] media)
        {
            _media = media;
            _listener.Start();
            Url = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/live.ts";
            _acceptTask = AcceptAsync();
        }

        /// <summary>
        /// 不包含外部地址或凭据的测试源。
        /// </summary>
        public string Url { get; }
        /// <summary>
        /// 已建立连接总数。
        /// </summary>
        public int Connections => Volatile.Read(ref _connections);
        /// <summary>
        /// 后续连接是否立即断开。
        /// </summary>
        public bool RejectReconnects
        {
            get => Volatile.Read(ref _rejectReconnects) != 0;
            set => Volatile.Write(ref _rejectReconnects, value ? 1 : 0);
        }

        /// <summary>
        /// 生成包含重复流头的短直播测试素材。
        /// </summary>
        public static async Task<LiveServer> CreateAsync()
        {
            var mediaPath = Path.Combine(Path.GetTempPath(), $"FluxMediaNetwork-{Guid.NewGuid():N}.ts");
            try
            {
                var start = new ProcessStartInfo(Path.Combine(FFmpegFactAttribute.NativeRoot, FFmpegLibraries.FFmpegExecutableName))
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true
                };
                foreach (var argument in new[] { "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "testsrc2=size=64x64:rate=10:duration=3", "-an", "-c:v", "libx264", "-preset", "ultrafast", "-bf", "0", "-g", "10", "-f", "mpegts", "-y", mediaPath })
                    start.ArgumentList.Add(argument);
                using var process = Process.Start(start)!;
                var error = await process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                Assert.True(process.ExitCode == 0, error);
                return new LiveServer(await File.ReadAllBytesAsync(mediaPath));
            }
            finally { File.Delete(mediaPath); }
        }

        /// <summary>
        /// 结束首次连接以模拟直播 EOF。
        /// </summary>
        public void DisconnectFirst() => Volatile.Write(ref _disconnectRequested, 1);

        /// <summary>
        /// 接收连接并记录全部处理任务。
        /// </summary>
        private async Task AcceptAsync()
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                    var number = Interlocked.Increment(ref _connections);
                    _clients.Add(ServeAsync(client, number));
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        }

        /// <summary>
        /// 按块发送视频，重连失败模式下立即关闭连接。
        /// </summary>
        private async Task ServeAsync(TcpClient client, int number)
        {
            using (client)
            {
                try
                {
                    var stream = client.GetStream();
                    using var request = new StreamReader(stream, Encoding.ASCII, false, leaveOpen: true);
                    while (!string.IsNullOrEmpty(await request.ReadLineAsync(_stop.Token))) { }
                    // 拒绝协议层的首次自重连，确保测试会进入会话恢复路径。
                    if (number == 2 || number > 1 && RejectReconnects)
                    {
                        await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"), _stop.Token);
                        return;
                    }
                    await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: video/mp2t\r\nConnection: close\r\n\r\n"), _stop.Token);
                    while (!_stop.IsCancellationRequested)
                    {
                        for (var offset = 0; offset < _media.Length; offset += 1316)
                        {
                            await stream.WriteAsync(_media.AsMemory(offset, Math.Min(1316, _media.Length - offset)), _stop.Token);
                            await Task.Delay(TimeSpan.FromSeconds(3d * 1316 / _media.Length), _stop.Token);
                        }
                        // 此用例验证恢复与录制转移，完整分段后断线，避免同时引入截断码流。
                        if (number == 1 && Volatile.Read(ref _disconnectRequested) != 0) return;
                    }
                }
                catch (Exception exception) when (exception is IOException or SocketException or ObjectDisposedException or OperationCanceledException) { }
            }
        }

        /// <summary>
        /// 关闭监听与客户端，等待后台任务退出。
        /// </summary>
        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            _listener.Stop();
            await _acceptTask;
            await Task.WhenAll(_clients).WaitAsync(TimeSpan.FromSeconds(3));
            _stop.Dispose();
        }
    }
}
