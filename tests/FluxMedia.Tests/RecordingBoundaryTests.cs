using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using FluxMedia.Core.Configuration;
using FluxMedia.Playback;

namespace FluxMedia.Tests;

/// <summary>
/// 录制边界测试。
/// </summary>
public sealed class RecordingBoundaryTests
{
    /// <summary>
    /// 验证暂停、恢复和释放后录制文件仍可解码。
    /// </summary>
    [Fact]
    public async Task Recording_PauseResumeAndDispose_FinalizesDecodableOutput()
    {
        var (root, exe) = ResolveFFmpeg();
        if (!File.Exists(exe))
        {
            return;
        }

        var temp = CreateTempDirectory();
        var source = Path.Combine(temp, "source.mp4");
        var output = Path.Combine(temp, "pause-resume.mp4");
        var player = FluxEngine.CreatePlayer();
        try
        {
            await RunProcessAsync(exe, $"-hide_banner -loglevel error -f lavfi -i testsrc=size=128x72:rate=15:duration=5 -f lavfi -i sine=sample_rate=48000:duration=5 -shortest -c:v libx264 -g 15 -c:a aac -pix_fmt yuv420p -y \"{source}\"");
            Assert.True((await player.OpenAndPlayAsync(source, Options(root))).IsSuccess);
            Assert.True((await player.StartRecordingAsync(output)).IsSuccess);
            await Task.Delay(700);
            Assert.True((await player.PauseAsync()).IsSuccess);
            await Task.Delay(300);
            Assert.True((await player.ResumeAsync()).IsSuccess);
            await Task.Delay(700);

            await player.DisposeAsync();
            Assert.True(File.Exists(output));
            await RunProcessAsync(exe, $"-hide_banner -loglevel error -i \"{output}\" -f null -");
        }
        finally
        {
            await player.DisposeAsync();
            TryDeleteDirectory(temp);
        }
    }

    /// <summary>
    /// 验证不兼容的录制容器不会破坏播放。
    /// </summary>
    [Fact]
    public async Task Recording_IncompatibleContainerFailsWithoutBreakingPlayback()
    {
        var (root, exe) = ResolveFFmpeg();
        if (!File.Exists(exe))
        {
            return;
        }

        var temp = CreateTempDirectory();
        var source = Path.Combine(temp, "source.mp4");
        var output = Path.Combine(temp, "invalid.wav");
        await using var player = FluxEngine.CreatePlayer();
        try
        {
            await RunProcessAsync(exe, $"-hide_banner -loglevel error -f lavfi -i testsrc=size=128x72:rate=15:duration=3 -an -c:v libx264 -pix_fmt yuv420p -y \"{source}\"");
            Assert.True((await player.OpenAndPlayAsync(source, Options(root))).IsSuccess);

            var recording = await player.StartRecordingAsync(output);
            Assert.False(recording.IsSuccess);
            Assert.Equal(PlaybackError.RecordingFailed, recording.Error);
            Assert.Equal(PlaybackState.Playing, player.State);
        }
        finally
        {
            await player.StopAsync();
            TryDeleteDirectory(temp);
        }
    }

    /// <summary>
    /// 验证写入配额失败只停止录制，播放继续。
    /// </summary>
    [Fact]
    public async Task Recording_WriteQuotaFailureStopsRecordingButPlaybackContinues()
    {
        var (root, exe) = ResolveFFmpeg();
        if (!File.Exists(exe))
        {
            return;
        }

        var temp = CreateTempDirectory();
        var source = Path.Combine(temp, "source.mp4");
        var output = Path.Combine(temp, "quota-failure.mp4");
        var firstFrame = new TaskCompletionSource<PlaybackVideoFrameEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var player = FluxEngine.CreatePlayer();
        try
        {
            await RunProcessAsync(exe, $"-hide_banner -loglevel error -f lavfi -i testsrc=size=128x72:rate=20:duration=5 -an -c:v libx264 -g 20 -pix_fmt yuv420p -y \"{source}\"");
            player.VideoFrameReady += (_, args) => firstFrame.TrySetResult(args);
            Assert.True((await player.OpenAndPlayAsync(source, Options(root))).IsSuccess);
            await firstFrame.Task.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.True((await player.StartRecordingAsync(new PlaybackRecordingOptions
            {
                OutputPath = output,
                SimulatedWriteQuotaBytes = 1
            })).IsSuccess);

            var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5);
            while (player.GetRecordingDiagnosticInfo().State != PlaybackRecordingState.Failed &&
                   DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(50);
            }

            var diagnostic = player.GetRecordingDiagnosticInfo();
            Assert.Equal(PlaybackRecordingState.Failed, diagnostic.State);
            Assert.Contains("quota", diagnostic.LastError ?? string.Empty, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(PlaybackState.Playing, player.State);
            Assert.True(player.GetDiagnosticInfo().DecodedFrameCount > 0);

            var stop = await player.StopRecordingAsync();
            Assert.False(stop.IsSuccess);
            Assert.Equal(PlaybackError.RecordingFailed, stop.Error);
        }
        finally
        {
            await player.StopAsync();
            TryDeleteDirectory(temp);
        }
    }

    /// <summary>
    /// 验证 HTTP 断线重连后的录制文件仍可解码。
    /// </summary>
    [Fact]
    public async Task Recording_HttpDisconnectReconnect_RemainsDecodable()
    {
        var (root, exe) = ResolveFFmpeg();
        if (!File.Exists(exe))
        {
            return;
        }

        var temp = CreateTempDirectory();
        var source = Path.Combine(temp, "network-source.mp4");
        var output = Path.Combine(temp, "network-reconnect.mp4");
        var firstFrame = new TaskCompletionSource<PlaybackVideoFrameEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var player = FluxEngine.CreatePlayer();
        try
        {
            await RunProcessAsync(
                exe, $"-hide_banner -loglevel error -f lavfi -i testsrc2=size=640x360:rate=20:duration=12 -f lavfi -i sine=frequency=880:sample_rate=48000:duration=12 -shortest -c:v libx264 -preset ultrafast -b:v 4M -g 20 -c:a aac -movflags +faststart -pix_fmt yuv420p -y \"{source}\"");
            using var server = ReconnectingHttpFileServer.Start(source, disconnectAfterBytes: 1024 * 1024);
            player.VideoFrameReady += (_, args) => firstFrame.TrySetResult(args);

            var open = await player.OpenAndPlayAsync(server.Url, new PlaybackOptions
            {
                NetworkTimeout = TimeSpan.FromSeconds(3),
                NetworkReconnectAttempts = 3,
                Network = NetworkPlaybackOptions.Compatible() with
                {
                    OpenTimeout = TimeSpan.FromSeconds(5),
                    ReadTimeout = TimeSpan.FromSeconds(3),
                    ReconnectEnabled = true,
                    ReconnectDelayMax = 1
                },
                NativeLibraries = new NativeLibraryOptions { FFmpegRootDirectory = root }
            });
            Assert.True(open.IsSuccess, open.Message);
            await firstFrame.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True((await player.StartRecordingAsync(output)).IsSuccess);

            server.TriggerDisconnect();
            var reconnectDeadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(10);
            while (server.AcceptedConnectionCount < 2 && DateTimeOffset.UtcNow < reconnectDeadline)
            {
                await Task.Delay(50);
            }

            Assert.True(server.AcceptedConnectionCount >= 2, "FFmpeg did not reconnect after the controlled HTTP disconnect.");
            await Task.Delay(1200);
            var stop = await player.StopRecordingAsync();
            Assert.True(stop.IsSuccess, stop.Message);
            Assert.Equal(PlaybackRecordingState.Completed, player.GetRecordingDiagnosticInfo().State);
            Assert.Equal(PlaybackState.Playing, player.State);
            await RunProcessAsync(exe, $"-hide_banner -loglevel error -i \"{output}\" -f null -");
        }
        finally
        {
            await player.StopAsync();
            TryDeleteDirectory(temp);
        }
    }

    /// <summary>
    /// 创建使用测试原生库目录的播放配置。
    /// </summary>
    private static PlaybackOptions Options(string root) => new()
    {
        NativeLibraries = new NativeLibraryOptions { FFmpegRootDirectory = root }
    };

    /// <summary>
    /// 查找测试使用的 FFmpeg 目录和可执行文件。
    /// </summary>
    private static (string Root, string Exe) ResolveFFmpeg()
    {
        var root = FFmpegFactAttribute.NativeRoot;
        return (root, Path.Combine(root, FFmpegLibraries.FFmpegExecutableName));
    }

    /// <summary>
    /// 为当前测试创建独立临时目录。
    /// </summary>
    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "FluxMediaTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>
    /// 尝试删除测试目录，忽略清理失败。
    /// </summary>
    private static void TryDeleteDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch
        {
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
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60));
        Assert.True(process.ExitCode == 0, $"Process failed with exit code {process.ExitCode}.\n{stdout}\n{stderr}");
    }

    /// <summary>
    /// 重连 HTTP 文件服务器。
    /// </summary>
    private sealed class ReconnectingHttpFileServer : IDisposable
    {
        /// <summary>
        /// 监听器。
        /// </summary>
        private readonly TcpListener _listener;

        /// <summary>
        /// 文件路径。
        /// </summary>
        private readonly string _filePath;

        /// <summary>
        /// 断开之后字节。
        /// </summary>
        private readonly long _disconnectAfterBytes;

        /// <summary>
        /// 取消源。
        /// </summary>
        private readonly CancellationTokenSource _cts = new();

        /// <summary>
        /// 断开同步锁。
        /// </summary>
        private readonly ManualResetEventSlim _disconnectGate = new(false);

        /// <summary>
        /// 服务任务。
        /// </summary>
        private readonly Task _serveTask;

        /// <summary>
        /// 已接受连接数量。
        /// </summary>
        private int _acceptedConnectionCount;

        /// <summary>
        /// 初始化重连 HTTP 文件服务器所需的状态和资源。
        /// </summary>
        private ReconnectingHttpFileServer(TcpListener listener, string filePath, long disconnectAfterBytes)
        {
            _listener = listener;
            _filePath = filePath;
            _disconnectAfterBytes = disconnectAfterBytes;
            Url = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/media.mp4";
            _serveTask = Task.Run(ServeAsync);
        }

        /// <summary>
        /// 地址。
        /// </summary>
        public string Url { get; }

        /// <summary>
        /// 已接受连接数量。
        /// </summary>
        public int AcceptedConnectionCount => Volatile.Read(ref _acceptedConnectionCount);

        /// <summary>
        /// 创建容量为一的最新帧输入队列。
        /// </summary>
        public static ReconnectingHttpFileServer Start(string filePath, long disconnectAfterBytes)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return new ReconnectingHttpFileServer(listener, filePath, disconnectAfterBytes);
        }

        /// <summary>
        /// 通知本地测试服务器主动断开连接。
        /// </summary>
        public void TriggerDisconnect() => _disconnectGate.Set();

        /// <summary>
        /// 释放当前对象持有的资源。
        /// </summary>
        public void Dispose()
        {
            _cts.Cancel();
            _disconnectGate.Set();
            _listener.Stop();
            try
            {
                _serveTask.Wait(TimeSpan.FromSeconds(2));
            }
            catch
            {
            }
            _disconnectGate.Dispose();
            _cts.Dispose();
        }

        /// <summary>
        /// 循环接受本地 HTTP 测试请求。
        /// </summary>
        private async Task ServeAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (ObjectDisposedException)
                {
                    return;
                }

                var connectionNumber = Interlocked.Increment(ref _acceptedConnectionCount);
                _ = Task.Run(() => HandleClientAsync(client, connectionNumber, _cts.Token));
            }
        }

        /// <summary>
        /// 处理单个 HTTP 客户端的媒体读取请求。
        /// </summary>
        private async Task HandleClientAsync(TcpClient client, int connectionNumber, CancellationToken cancellationToken)
        {
            using var _ = client;
            using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, false, leaveOpen: true);
            var requestLine = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(requestLine))
            {
                return;
            }

            var rangeStart = 0L;
            string? line;
            while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false)))
            {
                if (!line.StartsWith("Range:", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var value = line[(line.IndexOf("bytes=", StringComparison.OrdinalIgnoreCase) + 6)..]
                    .Split('-', 2)[0];
                long.TryParse(value, out rangeStart);
            }

            var fileLength = new FileInfo(_filePath).Length;
            rangeStart = Math.Clamp(rangeStart, 0, Math.Max(0, fileLength - 1));
            var partial = rangeStart > 0;
            var contentLength = fileLength - rangeStart;
            var header =
                $"HTTP/1.1 {(partial ? "206 Partial Content" : "200 OK")}\r\n" +
                "Content-Type: video/mp4\r\n" +
                "Accept-Ranges: bytes\r\n" +
                $"Content-Length: {contentLength}\r\n" +
                (partial ? $"Content-Range: bytes {rangeStart}-{fileLength - 1}/{fileLength}\r\n" : string.Empty) +
                "Connection: close\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(header), cancellationToken).ConfigureAwait(false);

            await using var file = File.OpenRead(_filePath);
            file.Position = rangeStart;
            var remaining = connectionNumber == 1
                ? Math.Min(_disconnectAfterBytes, contentLength)
                : contentLength;
            var buffer = new byte[32 * 1024];
            while (remaining > 0)
            {
                var read = await file.ReadAsync(
                    buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), cancellationToken).ConfigureAwait(false);
                if (read <= 0)
                {
                    return;
                }

                await stream.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                remaining -= read;
                await Task.Delay(2, cancellationToken).ConfigureAwait(false);
            }

            if (connectionNumber == 1)
            {
                while (!_disconnectGate.IsSet && !cancellationToken.IsCancellationRequested)
                {
                    await Task.Delay(20, cancellationToken).ConfigureAwait(false);
                }
            }
        }
    }
}
