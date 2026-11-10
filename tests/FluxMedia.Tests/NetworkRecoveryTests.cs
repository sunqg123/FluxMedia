using System.Net;
using System.Net.Sockets;
using FluxMedia.Core.Configuration;
using FluxMedia.Core.Models;
using FluxMedia.Playback;

namespace FluxMedia.Tests;

/// <summary>
/// 验证网络打开操作可被停止。
/// </summary>
public sealed class NetworkRecoveryTests
{
    /// <summary>
    /// 停止能中断正在等待服务器响应的打开操作。
    /// </summary>
    [FFmpegFact]
    public async Task StopDuringOpen_CancelsWithoutWaitingForOpenTimeout()
    {
        FluxEngine.Shutdown();
        FluxEngine.Start(new FluxEngineOptions { FFmpegPath = FFmpegFactAttribute.NativeRoot });
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
    /// 创建短超时的软件解码配置。
    /// </summary>
    private static PlaybackOptions CreateOptions() => new()
    {
        VideoAcceleration = VideoAccelerationMode.Disabled,
        NetworkReconnectAttempts = 3,
        Network = new NetworkPlaybackOptions
        {
            OpenTimeout = TimeSpan.FromSeconds(2),
            ReadTimeout = TimeSpan.FromSeconds(2),
            ReconnectDelayMax = 0
        }
    };
}
