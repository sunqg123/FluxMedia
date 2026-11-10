using System.Diagnostics;
using FluxMedia.Core.Configuration;
using FluxMedia.Playback;

namespace FluxMedia.Tests;

/// <summary>
/// Windows 媒体兼容性矩阵测试。
/// </summary>
public sealed class WindowsMediaCompatibilityMatrixTests
{
    /// <summary>
    /// 提供不同容器和编码组合的兼容性用例。
    /// </summary>
    public static IEnumerable<object[]> PlaybackCases()
    {
        yield return ["h264-aac.mp4", "-f lavfi -i testsrc=size=128x72:rate=15:duration=2 -f lavfi -i sine=frequency=440:sample_rate=48000:duration=2 -shortest -c:v libx264 -c:a aac -pix_fmt yuv420p"];
        yield return ["h265-aac.mkv", "-f lavfi -i testsrc=size=128x72:rate=15:duration=2 -f lavfi -i sine=frequency=550:sample_rate=48000:duration=2 -shortest -c:v libx265 -x265-params log-level=error -c:a aac -pix_fmt yuv420p"];
        yield return ["h264-mp3.mkv", "-f lavfi -i testsrc=size=128x72:rate=15:duration=2 -f lavfi -i sine=frequency=660:sample_rate=44100:duration=2 -shortest -c:v libx264 -c:a libmp3lame -pix_fmt yuv420p"];
        yield return ["h264-aac.ts", "-f lavfi -i testsrc=size=128x72:rate=15:duration=2 -f lavfi -i sine=frequency=770:sample_rate=48000:duration=2 -shortest -c:v libx264 -c:a aac -pix_fmt yuv420p -f mpegts"];
        yield return ["long-gop.mp4", "-f lavfi -i testsrc=size=128x72:rate=15:duration=4 -an -c:v libx264 -g 300 -keyint_min 300 -sc_threshold 0 -pix_fmt yuv420p"];
        yield return ["no-timestamps.h264", "-f lavfi -i testsrc=size=128x72:rate=15:duration=2 -an -c:v libx264 -g 15 -pix_fmt yuv420p -f h264"];
    }

    /// <summary>
    /// 验证 Windows 上各媒体兼容性用例都能输出视频帧。
    /// </summary>
    [Theory]
    [MemberData(nameof(PlaybackCases))]
    public async Task Windows_PlaybackMatrix_ProducesVideoFrames(string fileName, string ffmpegArguments)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var (ffmpegRoot, ffmpegExe) = ResolveFFmpeg();
        if (!File.Exists(ffmpegExe))
        {
            return;
        }

        var tempDir = CreateTempDirectory();
        var mediaPath = Path.Combine(tempDir, fileName);
        var firstFrame = new TaskCompletionSource<PlaybackVideoFrameEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var player = FluxEngine.CreatePlayer();
        try
        {
            await RunProcessAsync(ffmpegExe, $"-hide_banner -loglevel error {ffmpegArguments} -y \"{mediaPath}\"");
            player.VideoFrameReady += (_, args) => firstFrame.TrySetResult(args);
            var open = await player.OpenAndPlayAsync(mediaPath, new PlaybackOptions
            {
                NativeLibraries = new NativeLibraryOptions { FFmpegRootDirectory = ffmpegRoot }
            });
            Assert.True(open.IsSuccess, open.Message);
            var frame = await firstFrame.Task.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.True(frame.Frame.Format.Width > 0);
            Assert.True(frame.Frame.Format.Height > 0);
            Assert.False(player.GetDiagnosticInfo().Health.IsStalled);
        }
        finally
        {
            await player.StopAsync();
            TryDeleteDirectory(tempDir);
        }
    }

    /// <summary>
    /// 验证截断的 MP4 文件在打开超时内失败。
    /// </summary>
    [Fact]
    public async Task Windows_TruncatedMp4_FailsWithinOpenTimeout()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var (ffmpegRoot, ffmpegExe) = ResolveFFmpeg();
        if (!File.Exists(ffmpegExe))
        {
            return;
        }

        var tempDir = CreateTempDirectory();
        var validPath = Path.Combine(tempDir, "valid.mp4");
        var damagedPath = Path.Combine(tempDir, "damaged.mp4");
        await using var player = FluxEngine.CreatePlayer();
        try
        {
            await RunProcessAsync(
                ffmpegExe, $"-hide_banner -loglevel error -f lavfi -i testsrc=size=128x72:rate=15:duration=2 -an -c:v libx264 -pix_fmt yuv420p -y \"{validPath}\"");
            var bytes = await File.ReadAllBytesAsync(validPath);
            await File.WriteAllBytesAsync(damagedPath, bytes.AsSpan(0, Math.Min(1024, bytes.Length)).ToArray());

            var exception = await Assert.ThrowsAsync<PlaybackException>(
                () => player.OpenAsync(damagedPath, new PlaybackOptions
                {
                    OpenTimeout = TimeSpan.FromSeconds(2),
                    NativeLibraries = new NativeLibraryOptions { FFmpegRootDirectory = ffmpegRoot }
                }).AsTask());
            Assert.Equal(PlaybackError.OpenFailed, exception.PlaybackError);
            Assert.Equal(PlaybackState.Failed, player.State);
        }
        finally
        {
            await player.StopAsync();
            TryDeleteDirectory(tempDir);
        }
    }

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
}
