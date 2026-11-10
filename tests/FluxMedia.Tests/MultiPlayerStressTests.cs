using System.Diagnostics;
using System.Globalization;
using FluxMedia.Core.Configuration;
using FluxMedia.Playback;

namespace FluxMedia.Tests;

/// <summary>
/// 多播放器压力测试。
/// </summary>
public sealed class MultiPlayerStressTests
{
    /// <summary>
    /// 验证三个播放器并发播放、跳转和录制时相互独立。
    /// </summary>
    [Fact]
    public async Task ThreePlayers_ConcurrentPlaybackSeekAndRecording_RemainIndependent()
    {
        var ffmpegRoot = FFmpegFactAttribute.NativeRoot;
        var ffmpegExe = Path.Combine(ffmpegRoot, FFmpegLibraries.FFmpegExecutableName);
        if (!File.Exists(ffmpegExe))
        {
            return;
        }

        var tempDir = Path.Combine(Path.GetTempPath(), "FluxMediaTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var mediaPath = Path.Combine(tempDir, "multi-player-stress.mp4");
        var players = new List<Player>();

        try
        {
            await RunProcessAsync(
                ffmpegExe, $"-hide_banner -loglevel error -f lavfi -i testsrc=size=160x90:rate=20:duration=12 -f lavfi -i sine=frequency=440:sample_rate=48000:duration=12 -shortest -c:v libx264 -g 20 -c:a aac -pix_fmt yuv420p -y \"{mediaPath}\"");

            var options = new PlaybackOptions
            {
                NativeLibraries = new NativeLibraryOptions { FFmpegRootDirectory = ffmpegRoot }
            };
            var cycles = Math.Clamp(
                int.TryParse(Environment.GetEnvironmentVariable("FLUXMEDIA_SOAK_CYCLES"), out var configuredCycles)
                    ? configuredCycles
                    : 1, 1, 100);
            for (var soakCycle = 0; soakCycle < cycles; soakCycle++)
            {
                var recordingPath = Path.Combine(tempDir, $"multi-player-recording-{soakCycle}.mkv");
                for (var index = 0; index < 3; index++)
                {
                    var player = FluxEngine.CreatePlayer();
                    players.Add(player);
                    Assert.True((await player.OpenAndPlayAsync(mediaPath, options)).IsSuccess);
                }

                Assert.True((await players[0].StartRecordingAsync(recordingPath)).IsSuccess);
                for (var commandCycle = 0; commandCycle < 4; commandCycle++)
                {
                    var target = TimeSpan.FromSeconds(1 + commandCycle * 2);
                    var operations = players.Select(async (player, index) =>
                    {
                        await player.PauseAsync();
                        Assert.Equal(PlaybackState.Paused, player.State);
                        var seek = await player.SeekAsync(target + TimeSpan.FromMilliseconds(index * 150));
                        Assert.True(seek.IsSuccess, seek.Message);
                        var resume = await player.ResumeAsync();
                        Assert.True(resume.IsSuccess, resume.Message);
                    });
                    await Task.WhenAll(operations).WaitAsync(TimeSpan.FromSeconds(20));
                    await Task.Delay(250);
                    AppendSoakDiagnostics(players, soakCycle, commandCycle);
                }

                var stopRecording = await players[0].StopRecordingAsync();
                Assert.True(stopRecording.IsSuccess, stopRecording.Message);
                Assert.Equal(PlaybackRecordingState.Completed, players[0].GetRecordingDiagnosticInfo().State);
                Assert.All(players, player =>
                {
                    var diagnostic = player.GetDiagnosticInfo();
                    Assert.Equal(PlaybackState.Playing, player.State);
                    Assert.True(diagnostic.DecodedFrameCount > 0);
                    Assert.False(diagnostic.LastSeek.InProgress);
                    Assert.False(diagnostic.Health.IsStalled, diagnostic.Health.Reason);
                });
                Assert.Equal(3, players.Select(static player => player.InstanceId).Distinct().Count());
                await RunProcessAsync(ffmpegExe, $"-hide_banner -loglevel error -i \"{recordingPath}\" -f null -");

                foreach (var player in players)
                {
                    await player.DisposeAsync();
                }

                players.Clear();
            }
        }
        finally
        {
            foreach (var player in players)
            {
                await player.DisposeAsync();
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
    /// 将多播放器长稳诊断追加到结果文件。
    /// </summary>
    private static void AppendSoakDiagnostics(
        IReadOnlyList<Player> players, int soakCycle, int commandCycle)
    {
        var path = Environment.GetEnvironmentVariable("FLUXMEDIA_SOAK_DIAGNOSTICS_PATH");
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        if (!File.Exists(path))
        {
            File.AppendAllText(
                path, "Timestamp,Cycle,CommandCycle,PlayerId,State,WorkingSetBytes,PrivateBytes,HandleCount,ThreadCount,VideoTimeMs,AudioTimeMs,AudioVideoDriftMs,VideoBufferedMs,DecodedFrames,DroppedFrames,AudioFrames,AudioBackend,HardwareBackend,HealthStalled" +
                Environment.NewLine);
        }

        using var process = Process.GetCurrentProcess();
        foreach (var player in players)
        {
            var diagnostic = player.GetDiagnosticInfo();
            var values = new[]
            {
                DateTimeOffset.UtcNow.ToString("O",CultureInfo.InvariantCulture),
                soakCycle.ToString(CultureInfo.InvariantCulture),
                commandCycle.ToString(CultureInfo.InvariantCulture),
                player.InstanceId.ToString(),
                player.State.ToString(),
                process.WorkingSet64.ToString(CultureInfo.InvariantCulture),
                process.PrivateMemorySize64.ToString(CultureInfo.InvariantCulture),
                process.HandleCount.ToString(CultureInfo.InvariantCulture),
                process.Threads.Count.ToString(CultureInfo.InvariantCulture),
                diagnostic.VideoCurrentTime?.TotalMilliseconds.ToString("0.###",CultureInfo.InvariantCulture) ?? string.Empty,
                diagnostic.AudioCurrentTime?.TotalMilliseconds.ToString("0.###",CultureInfo.InvariantCulture) ?? string.Empty,
                diagnostic.AudioVideoDrift?.TotalMilliseconds.ToString("0.###",CultureInfo.InvariantCulture) ?? string.Empty,
                diagnostic.VideoBufferedDuration?.TotalMilliseconds.ToString("0.###",CultureInfo.InvariantCulture) ?? string.Empty,
                diagnostic.DecodedFrameCount.ToString(CultureInfo.InvariantCulture),
                diagnostic.DroppedFrameCount.ToString(CultureInfo.InvariantCulture),
                diagnostic.AudioDecodedFrameCount.ToString(CultureInfo.InvariantCulture),
                Csv(diagnostic.AudioOutputDeviceState),
                Csv(diagnostic.VideoDecoderBackend),
                diagnostic.Health.IsStalled.ToString(CultureInfo.InvariantCulture)
            };
            File.AppendAllText(path, string.Join(",", values) + Environment.NewLine);
        }
    }

    /// <summary>
    /// 转义文本以安全写入 CSV 单元格。
    /// </summary>
    private static string Csv(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return value.Contains(',') || value.Contains('"')
            ? $"\"{value.Replace("\"", "\"\"")}\""
            : value;
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
}
