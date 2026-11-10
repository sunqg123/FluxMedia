using FluxMedia.Core.Configuration;
using FluxMedia.Playback;

namespace FluxMedia.Tests;

/// <summary>
/// 显式配置外部源时执行播放验证。
/// </summary>
public sealed class ExternalNetworkPlaybackTests
{
    /// <summary>
    /// 验证用户配置的 HLS 源。
    /// </summary>
    [ExternalStreamFact("FLUXMEDIA_HLS_TEST_URL")]
    public Task ConfiguredHlsStream_ProducesVideoFrames() => VerifyAsync("FLUXMEDIA_HLS_TEST_URL");

    /// <summary>
    /// 验证用户配置的 RTSP 源。
    /// </summary>
    [ExternalStreamFact("FLUXMEDIA_RTSP_TEST_URL")]
    public Task ConfiguredRtspStream_ProducesVideoFrames() => VerifyAsync("FLUXMEDIA_RTSP_TEST_URL");

    /// <summary>
    /// 打开配置源并等待真实视频帧，失败信息不包含地址。
    /// </summary>
    private static async Task VerifyAsync(string variable)
    {
        var firstFrame = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var player = FluxEngine.CreatePlayer();
        player.VideoFrameReady += (_, _) => firstFrame.TrySetResult();
        try
        {
            var result = await player.OpenAndPlayAsync(Environment.GetEnvironmentVariable(variable)!, new PlaybackOptions
            {
                Network = NetworkPlaybackOptions.Compatible(),
                NativeLibraries = new NativeLibraryOptions
                {
                    FFmpegRootDirectory = FFmpegFactAttribute.NativeRoot
                }
            });
            Assert.True(result.IsSuccess, "外部流未能开始播放。");
            await firstFrame.Task.WaitAsync(TimeSpan.FromSeconds(20));
            Assert.False(player.GetDiagnosticInfo().Health.IsStalled);
        }
        catch (Exception exception)
        {
            throw new Xunit.Sdk.XunitException($"外部流验证失败，异常类型：{exception.GetType().Name}。");
        }
    }
}

/// <summary>
/// 缺少外部地址时明确跳过，避免提前返回被计为通过。
/// </summary>
public sealed class ExternalStreamFactAttribute : FactAttribute
{
    /// <summary>
    /// 检查外部源环境变量。
    /// </summary>
    public ExternalStreamFactAttribute(string variable)
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(variable))) Skip = $"未配置 {variable}。";
        else if (!FFmpegLibraries.IsAvailable(FFmpegFactAttribute.NativeRoot)) Skip = "缺少与当前绑定匹配的 FFmpeg 本地依赖。";
    }
}
