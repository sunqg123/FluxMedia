using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using FluxMedia.Core.Configuration;
using FluxMedia.Core.Errors;
using FluxMedia.Core.Models;
using FluxMedia.Playback;

namespace FluxMedia.Tests;

/// <summary>
/// 播放会话视频管线测试。
/// </summary>
public sealed class PlaybackSessionVideoPipelineTests
{
    /// <summary>
    /// 验证播放通过共享解复用管线输出视频帧。
    /// </summary>
    [Fact]
    public async Task Player_PlayAsync_UsesSharedDemuxVideoPipelineToProduceFrames()
    {
        var ffmpegRoot = FFmpegFactAttribute.NativeRoot;
        var ffmpegExe = Path.Combine(ffmpegRoot, FFmpegLibraries.FFmpegExecutableName);
        if (!File.Exists(ffmpegExe))
        {
            return;
        }

        var tempDir = Path.Combine(Path.GetTempPath(), "FluxMediaTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var mediaPath = Path.Combine(tempDir, "session-shared-demux-video.mp4");
        var firstFrame = new TaskCompletionSource<PlaybackVideoFrameEventArgs>();

        await using var player = FluxEngine.CreatePlayer();
        try
        {
            await RunProcessAsync(
                ffmpegExe, $"-hide_banner -loglevel error -f lavfi -i testsrc=size=64x64:rate=5:duration=1 -an -pix_fmt yuv420p -y \"{mediaPath}\"");

            player.VideoFrameReady += (_, args) => firstFrame.TrySetResult(args);
            await player.OpenAsync(mediaPath, new PlaybackOptions
            {
                NativeLibraries = new NativeLibraryOptions
                {
                    FFmpegRootDirectory = ffmpegRoot
                }
            });
            var playResult = await player.PlayAsync();

            Assert.True(playResult.IsSuccess, playResult.Message);
            var frame = (await firstFrame.Task.WaitAsync(TimeSpan.FromSeconds(10))).Frame;
            Assert.Equal(64, frame.Format.Width);
            Assert.Equal(64, frame.Format.Height);
            Assert.True(player.GetDiagnosticInfo().DecodedFrameCount > 0);
        }
        finally
        {
            await player.StopAsync();
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
    /// 验证打开并播放能成功输出视频帧。
    /// </summary>
    [Fact]
    public async Task Player_OpenAndPlayAsync_OpensSourceAndProducesFrames()
    {
        var ffmpegRoot = FFmpegFactAttribute.NativeRoot;
        var ffmpegExe = Path.Combine(ffmpegRoot, FFmpegLibraries.FFmpegExecutableName);
        if (!File.Exists(ffmpegExe))
        {
            return;
        }

        var tempDir = Path.Combine(Path.GetTempPath(), "FluxMediaTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var mediaPath = Path.Combine(tempDir, "session-open-and-play.mp4");
        var firstFrame = new TaskCompletionSource<PlaybackVideoFrameEventArgs>();

        await using var player = FluxEngine.CreatePlayer();
        try
        {
            await RunProcessAsync(
                ffmpegExe, $"-hide_banner -loglevel error -f lavfi -i testsrc=size=80x60:rate=8:duration=1 -an -pix_fmt yuv420p -y \"{mediaPath}\"");

            player.VideoFrameReady += (_, args) => firstFrame.TrySetResult(args);
            var result = await player.OpenAndPlayAsync(mediaPath, new PlaybackOptions
            {
                NativeLibraries = new NativeLibraryOptions
                {
                    FFmpegRootDirectory = ffmpegRoot
                }
            });

            Assert.True(result.IsSuccess, result.Message);
            var frame = (await firstFrame.Task.WaitAsync(TimeSpan.FromSeconds(10))).Frame;
            Assert.Equal(PlaybackState.Playing, player.State);
            Assert.Equal(80, frame.Format.Width);
            Assert.Equal(60, frame.Format.Height);
        }
        finally
        {
            await player.StopAsync();
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
    /// 验证打开失败后仍可打开并播放有效媒体。
    /// </summary>
    [Fact]
    public async Task Player_OpenAsync_AfterFailedOpenCanRecoverAndPlayValidSource()
    {
        var ffmpegRoot = FFmpegFactAttribute.NativeRoot;
        var ffmpegExe = Path.Combine(ffmpegRoot, FFmpegLibraries.FFmpegExecutableName);
        if (!File.Exists(ffmpegExe))
        {
            return;
        }

        var tempDir = Path.Combine(Path.GetTempPath(), "FluxMediaTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var invalidPath = Path.Combine(tempDir, "not-media.txt");
        var mediaPath = Path.Combine(tempDir, "session-recover-after-failed-open.mp4");
        var firstFrame = new TaskCompletionSource<PlaybackVideoFrameEventArgs>();

        await using var player = FluxEngine.CreatePlayer();
        try
        {
            await File.WriteAllTextAsync(invalidPath, "not a media file");
            await RunProcessAsync(
                ffmpegExe, $"-hide_banner -loglevel error -f lavfi -i testsrc=size=72x54:rate=8:duration=1 -an -pix_fmt yuv420p -y \"{mediaPath}\"");

            await Assert.ThrowsAsync<PlaybackException>(async () =>
                await player.OpenAsync(invalidPath, new PlaybackOptions
                {
                    NativeLibraries = new NativeLibraryOptions
                    {
                        FFmpegRootDirectory = ffmpegRoot
                    }
                }));
            Assert.Equal(PlaybackState.Failed, player.State);

            player.VideoFrameReady += (_, args) => firstFrame.TrySetResult(args);
            var result = await player.OpenAndPlayAsync(mediaPath, new PlaybackOptions
            {
                NativeLibraries = new NativeLibraryOptions
                {
                    FFmpegRootDirectory = ffmpegRoot
                }
            });

            Assert.True(result.IsSuccess, result.Message);
            var frame = (await firstFrame.Task.WaitAsync(TimeSpan.FromSeconds(10))).Frame;
            Assert.Equal(PlaybackState.Playing, player.State);
            Assert.Equal(72, frame.Format.Width);
            Assert.Equal(54, frame.Format.Height);
            Assert.Equal(mediaPath, player.Source?.Uri);
        }
        finally
        {
            await player.StopAsync();
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
    /// 验证音频解码消费共享解复用器的数据包。
    /// </summary>
    [Fact]
    public async Task Player_PlayAsync_UsesSharedDemuxAudioPackets()
    {
        var ffmpegRoot = FFmpegFactAttribute.NativeRoot;
        var ffmpegExe = Path.Combine(ffmpegRoot, FFmpegLibraries.FFmpegExecutableName);
        if (!File.Exists(ffmpegExe))
        {
            return;
        }

        var tempDir = Path.Combine(Path.GetTempPath(), "FluxMediaTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var mediaPath = Path.Combine(tempDir, "session-shared-demux-audio.mp4");
        var firstFrame = new TaskCompletionSource<PlaybackVideoFrameEventArgs>();
        var presentedFrames = 0;

        await using var player = FluxEngine.CreatePlayer();
        try
        {
            await RunProcessAsync(
                ffmpegExe, $"-hide_banner -loglevel error -f lavfi -i testsrc=size=64x64:rate=5:duration=1 -f lavfi -i sine=frequency=440:sample_rate=48000:duration=1 -shortest -pix_fmt yuv420p -y \"{mediaPath}\"");

            player.VideoFrameReady += (_, args) =>
            {
                Interlocked.Increment(ref presentedFrames);
                firstFrame.TrySetResult(args);
            };
            await player.OpenAsync(mediaPath, new PlaybackOptions
            {
                NativeLibraries = new NativeLibraryOptions
                {
                    FFmpegRootDirectory = ffmpegRoot
                }
            });
            var playResult = await player.PlayAsync();

            Assert.True(playResult.IsSuccess, playResult.Message);
            await firstFrame.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await Task.Delay(600);
            Assert.True(
                Volatile.Read(ref presentedFrames) >= 2, $"Expected multiple presented frames, received {presentedFrames}.");

            var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(10);
            PlaybackDiagnosticInfo diagnostic;
            do
            {
                await Task.Delay(50);
                diagnostic = player.GetDiagnosticInfo();
            } while (diagnostic.AudioDecodedFrameCount == 0 && DateTimeOffset.UtcNow < deadline);

            Assert.True(diagnostic.AudioDecodedFrameCount > 0, $"Audio worker state: {diagnostic.AudioWorkerState}, error: {diagnostic.LastAudioError}");
        }
        finally
        {
            await player.StopAsync();
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
    /// 验证暂停、恢复和停止时更新生命周期诊断。
    /// </summary>
    [Fact]
    public async Task Player_LocalPlayback_PauseResumeStop_UpdatesLifecycleDiagnostics()
    {
        var ffmpegRoot = FFmpegFactAttribute.NativeRoot;
        var ffmpegExe = Path.Combine(ffmpegRoot, FFmpegLibraries.FFmpegExecutableName);
        if (!File.Exists(ffmpegExe))
        {
            return;
        }

        var tempDir = Path.Combine(Path.GetTempPath(), "FluxMediaTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var mediaPath = Path.Combine(tempDir, "session-local-lifecycle.mp4");
        var frameCount = 0;
        var firstFrame = new TaskCompletionSource<PlaybackVideoFrameEventArgs>();

        await using var player = FluxEngine.CreatePlayer();
        try
        {
            await RunProcessAsync(
                ffmpegExe, $"-hide_banner -loglevel error -f lavfi -i testsrc=size=64x64:rate=8:duration=2 -an -pix_fmt yuv420p -y \"{mediaPath}\"");

            player.VideoFrameReady += (_, args) =>
            {
                Interlocked.Increment(ref frameCount);
                firstFrame.TrySetResult(args);
            };

            await player.OpenAsync(mediaPath, new PlaybackOptions
            {
                NativeLibraries = new NativeLibraryOptions
                {
                    FFmpegRootDirectory = ffmpegRoot
                }
            });
            Assert.Equal(PlaybackState.Ready, player.State);

            var playResult = await player.PlayAsync();
            Assert.True(playResult.IsSuccess, playResult.Message);
            await firstFrame.Task.WaitAsync(TimeSpan.FromSeconds(10));

            var pauseResult = await player.PauseAsync();
            Assert.True(pauseResult.IsSuccess, pauseResult.Message);
            Assert.Equal(PlaybackState.Paused, player.State);

            var resumeResult = await player.ResumeAsync();
            Assert.True(resumeResult.IsSuccess, resumeResult.Message);
            Assert.Equal(PlaybackState.Playing, player.State);

            await Task.Delay(250);
            await player.StopAsync();
            Assert.Equal(PlaybackState.Stopped, player.State);

            var diagnostic = player.GetDiagnosticInfo();
            Assert.True(frameCount > 0);
            Assert.True(diagnostic.Lifecycle.PlayCount >= 1);
            Assert.True(diagnostic.Lifecycle.PauseCount >= 1);
            Assert.True(diagnostic.Lifecycle.StopCount >= 1);
            Assert.NotNull(diagnostic.Lifecycle.FirstUiFrameAt);
            Assert.False(diagnostic.Health.IsStalled, diagnostic.Health.Reason);
        }
        finally
        {
            await player.StopAsync();
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
    /// 验证视频自然结束后进入播放完成状态。
    /// </summary>
    [Fact]
    public async Task Player_LocalPlayback_ReachesCompletedStateAtEndOfVideo()
    {
        var ffmpegRoot = FFmpegFactAttribute.NativeRoot;
        var ffmpegExe = Path.Combine(ffmpegRoot, FFmpegLibraries.FFmpegExecutableName);
        if (!File.Exists(ffmpegExe))
        {
            return;
        }

        var tempDir = Path.Combine(Path.GetTempPath(), "FluxMediaTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var mediaPath = Path.Combine(tempDir, "session-local-completed.mp4");
        var completed = new TaskCompletionSource<PlaybackState>();

        await using var player = FluxEngine.CreatePlayer();
        try
        {
            await RunProcessAsync(
                ffmpegExe, $"-hide_banner -loglevel error -f lavfi -i testsrc=size=64x64:rate=8:duration=1.2 -an -pix_fmt yuv420p -y \"{mediaPath}\"");

            player.StateChanged += (_, state) =>
            {
                if (state == PlaybackState.Completed)
                {
                    completed.TrySetResult(state);
                }
            };

            await player.OpenAsync(mediaPath, new PlaybackOptions
            {
                NativeLibraries = new NativeLibraryOptions
                {
                    FFmpegRootDirectory = ffmpegRoot
                }
            });

            var playResult = await player.PlayAsync();
            Assert.True(playResult.IsSuccess, playResult.Message);
            await completed.Task.WaitAsync(TimeSpan.FromSeconds(10));

            var diagnostic = player.GetDiagnosticInfo();
            Assert.Equal(PlaybackState.Completed, player.State);
            Assert.True(diagnostic.Lifecycle.NaturalCompletionCount >= 1);
            Assert.NotNull(diagnostic.Lifecycle.CompletedAt);
        }
        finally
        {
            await player.StopAsync();
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
    /// 验证自然结束后无需重新打开即可重播。
    /// </summary>
    [Fact]
    public async Task Player_LocalPlayback_CanReplayAfterCompletedWithoutReopen()
    {
        var ffmpegRoot = FFmpegFactAttribute.NativeRoot;
        var ffmpegExe = Path.Combine(ffmpegRoot, FFmpegLibraries.FFmpegExecutableName);
        if (!File.Exists(ffmpegExe))
        {
            return;
        }

        var tempDir = Path.Combine(Path.GetTempPath(), "FluxMediaTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var mediaPath = Path.Combine(tempDir, "session-local-replay-after-completed.mp4");
        var completedCount = 0;
        var firstPlaybackFrame = new TaskCompletionSource<PlaybackVideoFrameEventArgs>();
        var secondPlaybackFrame = new TaskCompletionSource<PlaybackVideoFrameEventArgs>();
        var firstCompleted = new TaskCompletionSource<PlaybackState>();
        var secondCompleted = new TaskCompletionSource<PlaybackState>();

        await using var player = FluxEngine.CreatePlayer();
        try
        {
            await RunProcessAsync(
                ffmpegExe, $"-hide_banner -loglevel error -f lavfi -i testsrc=size=64x64:rate=8:duration=1.2 -an -pix_fmt yuv420p -y \"{mediaPath}\"");

            player.VideoFrameReady += (_, args) =>
            {
                if (Volatile.Read(ref completedCount) == 0)
                {
                    firstPlaybackFrame.TrySetResult(args);
                }
                else
                {
                    secondPlaybackFrame.TrySetResult(args);
                }
            };

            player.StateChanged += (_, state) =>
            {
                if (state != PlaybackState.Completed)
                {
                    return;
                }

                if (Interlocked.Increment(ref completedCount) == 1)
                {
                    firstCompleted.TrySetResult(state);
                }
                else
                {
                    secondCompleted.TrySetResult(state);
                }
            };

            await player.OpenAsync(mediaPath, new PlaybackOptions
            {
                NativeLibraries = new NativeLibraryOptions
                {
                    FFmpegRootDirectory = ffmpegRoot
                }
            });

            var firstPlayResult = await player.PlayAsync();
            Assert.True(firstPlayResult.IsSuccess, firstPlayResult.Message);
            await firstPlaybackFrame.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await firstCompleted.Task.WaitAsync(TimeSpan.FromSeconds(10));

            var secondPlayResult = await player.PlayAsync();
            Assert.True(secondPlayResult.IsSuccess, secondPlayResult.Message);
            var replayFirstFrame = await secondPlaybackFrame.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.InRange(replayFirstFrame.Frame.Timestamp, TimeSpan.Zero, TimeSpan.FromMilliseconds(250));
            await secondCompleted.Task.WaitAsync(TimeSpan.FromSeconds(10));

            var diagnostic = player.GetDiagnosticInfo();
            Assert.Equal(PlaybackState.Completed, player.State);
            Assert.True(diagnostic.Lifecycle.PlayCount >= 2);
            Assert.True(diagnostic.Lifecycle.NaturalCompletionCount >= 2);
            Assert.False(diagnostic.Health.IsStalled, diagnostic.Health.Reason);
        }
        finally
        {
            await player.StopAsync();
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
    /// 验证停止后无需重新打开即可再次播放。
    /// </summary>
    [Fact]
    public async Task Player_LocalPlayback_CanReplayAfterStopWithoutReopen()
    {
        var ffmpegRoot = FFmpegFactAttribute.NativeRoot;
        var ffmpegExe = Path.Combine(ffmpegRoot, FFmpegLibraries.FFmpegExecutableName);
        if (!File.Exists(ffmpegExe))
        {
            return;
        }

        var tempDir = Path.Combine(Path.GetTempPath(), "FluxMediaTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var mediaPath = Path.Combine(tempDir, "session-local-replay-after-stop.mp4");
        var playbackGeneration = 0;
        var firstPlaybackFrame = new TaskCompletionSource<PlaybackVideoFrameEventArgs>();
        var secondPlaybackFrame = new TaskCompletionSource<PlaybackVideoFrameEventArgs>();

        await using var player = FluxEngine.CreatePlayer();
        try
        {
            await RunProcessAsync(
                ffmpegExe, $"-hide_banner -loglevel error -f lavfi -i testsrc=size=64x64:rate=8:duration=2 -an -pix_fmt yuv420p -y \"{mediaPath}\"");

            player.VideoFrameReady += (_, args) =>
            {
                if (Volatile.Read(ref playbackGeneration) == 1)
                {
                    firstPlaybackFrame.TrySetResult(args);
                }
                else if (Volatile.Read(ref playbackGeneration) == 2)
                {
                    secondPlaybackFrame.TrySetResult(args);
                }
            };

            await player.OpenAsync(mediaPath, new PlaybackOptions
            {
                NativeLibraries = new NativeLibraryOptions
                {
                    FFmpegRootDirectory = ffmpegRoot
                }
            });

            Interlocked.Exchange(ref playbackGeneration, 1);
            var firstPlayResult = await player.PlayAsync();
            Assert.True(firstPlayResult.IsSuccess, firstPlayResult.Message);
            await firstPlaybackFrame.Task.WaitAsync(TimeSpan.FromSeconds(10));

            await player.StopAsync();
            Assert.Equal(PlaybackState.Stopped, player.State);

            Interlocked.Exchange(ref playbackGeneration, 2);
            var secondPlayResult = await player.PlayAsync();
            Assert.True(secondPlayResult.IsSuccess, secondPlayResult.Message);
            await secondPlaybackFrame.Task.WaitAsync(TimeSpan.FromSeconds(10));

            var diagnostic = player.GetDiagnosticInfo();
            Assert.Equal(PlaybackState.Playing, player.State);
            Assert.True(diagnostic.Lifecycle.PlayCount >= 2);
            Assert.True(diagnostic.Lifecycle.StopCount >= 1);
            Assert.False(diagnostic.Health.IsStalled, diagnostic.Health.Reason);
        }
        finally
        {
            await player.StopAsync();
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
    /// 验证并发播放命令串行执行且能正常停止。
    /// </summary>
    [Fact]
    public async Task Player_ConcurrentCommands_AreSerializedAndStopCleanly()
    {
        var ffmpegRoot = FFmpegFactAttribute.NativeRoot;
        var ffmpegExe = Path.Combine(ffmpegRoot, FFmpegLibraries.FFmpegExecutableName);
        if (!File.Exists(ffmpegExe))
        {
            return;
        }

        var tempDir = Path.Combine(Path.GetTempPath(), "FluxMediaTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var mediaPath = Path.Combine(tempDir, "session-concurrent-commands.mp4");
        var firstFrame = new TaskCompletionSource<PlaybackVideoFrameEventArgs>();

        await using var player = FluxEngine.CreatePlayer();
        try
        {
            await RunProcessAsync(
                ffmpegExe, $"-hide_banner -loglevel error -f lavfi -i testsrc=size=64x64:rate=12:duration=3 -an -pix_fmt yuv420p -y \"{mediaPath}\"");

            player.VideoFrameReady += (_, args) => firstFrame.TrySetResult(args);
            await player.OpenAsync(mediaPath, new PlaybackOptions
            {
                NativeLibraries = new NativeLibraryOptions
                {
                    FFmpegRootDirectory = ffmpegRoot
                }
            });

            var playResult = await player.PlayAsync();
            Assert.True(playResult.IsSuccess, playResult.Message);
            await firstFrame.Task.WaitAsync(TimeSpan.FromSeconds(10));

            var commands = new[]
            {
                Task.Run(async () => await player.SeekAsync(TimeSpan.FromMilliseconds(500))),
                Task.Run(async () => await player.SetVolumeAsync(0.35)),
                Task.Run(async () => await player.PauseAsync()),
                Task.Run(async () => await player.PlayAsync()),
                Task.Run(async () =>
                {
                    await Task.Delay(50);
                    await player.StopAsync();
                    return PlaybackResult.Success();
                })
            };

            try { await Task.WhenAll(commands).WaitAsync(TimeSpan.FromSeconds(15)); }
            catch (OperationCanceledException) when (commands[^1].IsCompletedSuccessfully) { }
            await player.StopAsync();

            var diagnostic = player.GetDiagnosticInfo();
            Assert.Equal(PlaybackState.Stopped, player.State);
            Assert.False(diagnostic.Health.IsStalled, diagnostic.Health.Reason);
            Assert.True(diagnostic.Lifecycle.StopCount >= 1);
        }
        finally
        {
            await player.StopAsync();
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
    /// 验证超出范围的跳转目标被限制在媒体范围内。
    /// </summary>
    [Fact]
    public async Task Player_SeekAsync_ClampsOutOfRangePositions()
    {
        var ffmpegRoot = FFmpegFactAttribute.NativeRoot;
        var ffmpegExe = Path.Combine(ffmpegRoot, FFmpegLibraries.FFmpegExecutableName);
        if (!File.Exists(ffmpegExe))
        {
            return;
        }

        var tempDir = Path.Combine(Path.GetTempPath(), "FluxMediaTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var mediaPath = Path.Combine(tempDir, "session-seek-clamp.mp4");
        var firstFrame = new TaskCompletionSource<PlaybackVideoFrameEventArgs>();

        await using var player = FluxEngine.CreatePlayer();
        try
        {
            await RunProcessAsync(
                ffmpegExe, $"-hide_banner -loglevel error -f lavfi -i testsrc=size=64x64:rate=8:duration=3 -an -pix_fmt yuv420p -y \"{mediaPath}\"");

            player.VideoFrameReady += (_, args) => firstFrame.TrySetResult(args);
            await player.OpenAsync(mediaPath, new PlaybackOptions
            {
                NativeLibraries = new NativeLibraryOptions
                {
                    FFmpegRootDirectory = ffmpegRoot
                }
            });

            var playResult = await player.PlayAsync();
            Assert.True(playResult.IsSuccess, playResult.Message);
            await firstFrame.Task.WaitAsync(TimeSpan.FromSeconds(10));

            var negativeSeek = await player.SeekAsync(TimeSpan.FromSeconds(-30));
            Assert.True(negativeSeek.IsSuccess, negativeSeek.Message);
            Assert.Equal(TimeSpan.Zero, player.GetDiagnosticInfo().LastSeek.RequestedPosition);

            var beyondSeek = await player.SeekAsync(TimeSpan.FromMinutes(10));
            var diagnostic = player.GetDiagnosticInfo();
            Assert.True(beyondSeek.IsSuccess, beyondSeek.Message);
            Assert.Equal(player.Duration, diagnostic.LastSeek.RequestedPosition);
            Assert.False(diagnostic.Health.IsStalled, diagnostic.Health.Reason);
        }
        finally
        {
            await player.StopAsync();
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
    /// 验证跳转后恢复原有暂停或播放意图。
    /// </summary>
    [Fact]
    public async Task Player_SeekAsync_RestoresPausedAndPlayingIntent()
    {
        var ffmpegRoot = FFmpegFactAttribute.NativeRoot;
        var ffmpegExe = Path.Combine(ffmpegRoot, FFmpegLibraries.FFmpegExecutableName);
        if (!File.Exists(ffmpegExe))
        {
            return;
        }

        var tempDir = Path.Combine(Path.GetTempPath(), "FluxMediaTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var mediaPath = Path.Combine(tempDir, "session-seek-state.mp4");
        var firstFrame = new TaskCompletionSource<PlaybackVideoFrameEventArgs>();

        await using var player = FluxEngine.CreatePlayer();
        try
        {
            await RunProcessAsync(
                ffmpegExe, $"-hide_banner -loglevel error -f lavfi -i testsrc=size=64x64:rate=12:duration=5 -f lavfi -i sine=frequency=440:sample_rate=48000:duration=5 -shortest -pix_fmt yuv420p -y \"{mediaPath}\"");

            player.VideoFrameReady += (_, args) => firstFrame.TrySetResult(args);
            var play = await player.OpenAndPlayAsync(mediaPath, new PlaybackOptions
            {
                NativeLibraries = new NativeLibraryOptions { FFmpegRootDirectory = ffmpegRoot }
            });
            Assert.True(play.IsSuccess, play.Message);
            await firstFrame.Task.WaitAsync(TimeSpan.FromSeconds(10));

            await player.PauseAsync();
            var pausedSeek = await player.SeekAsync(TimeSpan.FromSeconds(2));
            Assert.True(pausedSeek.IsSuccess, pausedSeek.Message);
            Assert.Equal(PlaybackState.Paused, player.State);
            Assert.False(player.GetDiagnosticInfo().LastSeek.InProgress);

            await player.ResumeAsync();
            var playingSeek = await player.SeekAsync(TimeSpan.FromSeconds(3));
            Assert.True(playingSeek.IsSuccess, playingSeek.Message);
            Assert.Equal(PlaybackState.Playing, player.State);
            Assert.False(player.GetDiagnosticInfo().LastSeek.InProgress);
        }
        finally
        {
            await player.StopAsync();
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
    /// 验证缺少视频流时播放失败并清理已启动管线。
    /// </summary>
    [Fact]
    public async Task Player_PlayAsync_NoVideoStreamFailsWithoutLeavingPipelineRunning()
    {
        var ffmpegRoot = FFmpegFactAttribute.NativeRoot;
        var ffmpegExe = Path.Combine(ffmpegRoot, FFmpegLibraries.FFmpegExecutableName);
        if (!File.Exists(ffmpegExe))
        {
            return;
        }

        var tempDir = Path.Combine(Path.GetTempPath(), "FluxMediaTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var mediaPath = Path.Combine(tempDir, "session-audio-only.m4a");

        await using var player = FluxEngine.CreatePlayer();
        try
        {
            await RunProcessAsync(
                ffmpegExe, $"-hide_banner -loglevel error -f lavfi -i sine=frequency=440:sample_rate=48000:duration=1 -vn -y \"{mediaPath}\"");

            var failure = await Assert.ThrowsAsync<PlaybackException>(async () => await player.OpenAsync(mediaPath, new PlaybackOptions
            {
                NativeLibraries = new NativeLibraryOptions
                {
                    FFmpegRootDirectory = ffmpegRoot
                }
            }));
            var diagnostic = player.GetDiagnosticInfo();

            Assert.Equal(PlaybackError.OpenFailed, failure.PlaybackError);
            Assert.Equal(PlaybackState.Failed, player.State);
            Assert.False(diagnostic.WorkerRunning);
            Assert.False(diagnostic.Health.IsStalled, diagnostic.Health.Reason);
            Assert.Null(diagnostic.Network.DemuxerState);
        }
        finally
        {
            await player.StopAsync();
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
    /// 验证本地 HTTP 媒体地址可以输出视频帧。
    /// </summary>
    [Fact]
    public async Task Player_NetworkPlayback_LocalHttpUrlProducesFrames()
    {
        var ffmpegRoot = FFmpegFactAttribute.NativeRoot;
        var ffmpegExe = Path.Combine(ffmpegRoot, FFmpegLibraries.FFmpegExecutableName);
        if (!File.Exists(ffmpegExe))
        {
            return;
        }

        var tempDir = Path.Combine(Path.GetTempPath(), "FluxMediaTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var mediaPath = Path.Combine(tempDir, "session-network-local-http.mp4");
        var firstFrame = new TaskCompletionSource<PlaybackVideoFrameEventArgs>();

        await using var player = FluxEngine.CreatePlayer();
        try
        {
            await RunProcessAsync(
                ffmpegExe, $"-hide_banner -loglevel error -f lavfi -i testsrc=size=64x64:rate=8:duration=1.5 -an -pix_fmt yuv420p -movflags +faststart -y \"{mediaPath}\"");

            using var server = LocalHttpFileServer.Start(mediaPath);
            player.VideoFrameReady += (_, args) => firstFrame.TrySetResult(args);

            await player.OpenAsync(MediaSource.FromNetwork(server.Url), new PlaybackOptions
            {
                NativeLibraries = new NativeLibraryOptions
                {
                    FFmpegRootDirectory = ffmpegRoot
                }
            });

            var playResult = await player.PlayAsync();
            Assert.True(playResult.IsSuccess, playResult.Message);

            var frame = (await firstFrame.Task.WaitAsync(TimeSpan.FromSeconds(10))).Frame;
            var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5);
            PlaybackDiagnosticInfo diagnostic;
            do
            {
                await Task.Delay(25);
                diagnostic = player.GetDiagnosticInfo();
            } while (diagnostic.Network.LastPacketAt is null && DateTimeOffset.UtcNow < deadline);

            Assert.Equal(64, frame.Format.Width);
            Assert.Equal(64, frame.Format.Height);
            Assert.Equal(MediaSourceKind.NetworkStream, diagnostic.SourceKind);
            Assert.True(diagnostic.Network.IsNetworkSource);
            Assert.NotNull(diagnostic.Network.LastPacketAt);
            Assert.False(diagnostic.Health.IsStalled, diagnostic.Health.Reason);
            Assert.Equal(1, server.AcceptedConnectionCount);
        }
        finally
        {
            await player.StopAsync();
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
    /// 验证禁用重连的配置能反映在诊断信息中。
    /// </summary>
    [Fact]
    public async Task Player_NetworkPlayback_DisabledReconnectIsReflectedInDiagnostics()
    {
        var ffmpegRoot = FFmpegFactAttribute.NativeRoot;
        var ffmpegExe = Path.Combine(ffmpegRoot, FFmpegLibraries.FFmpegExecutableName);
        if (!File.Exists(ffmpegExe))
        {
            return;
        }

        var tempDir = Path.Combine(Path.GetTempPath(), "FluxMediaTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var mediaPath = Path.Combine(tempDir, "session-network-no-reconnect.mp4");
        var firstFrame = new TaskCompletionSource<PlaybackVideoFrameEventArgs>();

        await using var player = FluxEngine.CreatePlayer();
        try
        {
            await RunProcessAsync(
                ffmpegExe, $"-hide_banner -loglevel error -f lavfi -i testsrc=size=64x64:rate=8:duration=1 -an -pix_fmt yuv420p -movflags +faststart -y \"{mediaPath}\"");

            using var server = LocalHttpFileServer.Start(mediaPath);
            player.VideoFrameReady += (_, args) => firstFrame.TrySetResult(args);

            await player.OpenAsync(MediaSource.FromNetwork(server.Url), new PlaybackOptions
            {
                NetworkReconnectAttempts = 5,
                Network = NetworkPlaybackOptions.Compatible() with
                {
                    ReconnectEnabled = false
                },
                NativeLibraries = new NativeLibraryOptions
                {
                    FFmpegRootDirectory = ffmpegRoot
                }
            });

            var playResult = await player.PlayAsync();
            Assert.True(playResult.IsSuccess, playResult.Message);
            await firstFrame.Task.WaitAsync(TimeSpan.FromSeconds(10));

            var diagnostic = player.GetDiagnosticInfo();
            Assert.True(diagnostic.Network.IsNetworkSource);
            Assert.False(diagnostic.Network.ReconnectEnabled);
            Assert.Equal(0, diagnostic.Network.ConfiguredReconnectAttempts);
        }
        finally
        {
            await player.StopAsync();
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
    /// 验证 HTTP 读取停滞时记录超时诊断。
    /// </summary>
    [Fact]
    public async Task Player_NetworkPlayback_StalledHttpReadReportsTimeoutDiagnostics()
    {
        var ffmpegRoot = FFmpegFactAttribute.NativeRoot;
        var ffmpegExe = Path.Combine(ffmpegRoot, FFmpegLibraries.FFmpegExecutableName);
        if (!File.Exists(ffmpegExe))
        {
            return;
        }

        var tempDir = Path.Combine(Path.GetTempPath(), "FluxMediaTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var mediaPath = Path.Combine(tempDir, "session-network-stalled-http.mp4");
        var firstFrame = new TaskCompletionSource<PlaybackVideoFrameEventArgs>();

        await using var player = FluxEngine.CreatePlayer();
        try
        {
            await RunProcessAsync(
                ffmpegExe, $"-hide_banner -loglevel error -f lavfi -i testsrc=size=96x54:rate=24:duration=12 -an -pix_fmt yuv420p -movflags +faststart -y \"{mediaPath}\"");

            using var server = LocalHttpFileServer.Start(mediaPath, stallAfterBytes: 96 * 1024, stallDelay: TimeSpan.FromSeconds(8));
            player.VideoFrameReady += (_, args) => firstFrame.TrySetResult(args);

            await player.OpenAsync(MediaSource.FromNetwork(server.Url), new PlaybackOptions
            {
                NetworkTimeout = TimeSpan.FromMilliseconds(700),
                NetworkReconnectAttempts = 0,
                Network = NetworkPlaybackOptions.Compatible() with
                {
                    ReadTimeout = TimeSpan.FromMilliseconds(700),
                    ReconnectEnabled = false
                },
                NativeLibraries = new NativeLibraryOptions
                {
                    FFmpegRootDirectory = ffmpegRoot
                }
            });

            var playResult = await player.PlayAsync();
            Assert.True(playResult.IsSuccess, playResult.Message);
            await firstFrame.Task.WaitAsync(TimeSpan.FromSeconds(10));

            var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(10);
            PlaybackDiagnosticInfo diagnostic;
            do
            {
                await Task.Delay(100);
                diagnostic = player.GetDiagnosticInfo();
            } while (diagnostic.Network.ReadTimeoutCount == 0 &&
                     diagnostic.Network.LastNetworkError is null &&
                     diagnostic.Network.DemuxerExitReason is null &&
                     DateTimeOffset.UtcNow < deadline);

            Assert.True(diagnostic.Network.IsNetworkSource);
            Assert.False(diagnostic.Network.ReconnectEnabled);
            Assert.True(
                diagnostic.Network.ReadTimeoutCount > 0 ||
                diagnostic.Network.LastNetworkError is not null ||
                diagnostic.Network.DemuxerExitReason is not null, $"Network diagnostics did not report timeout. Demux={diagnostic.Network.DemuxerState}, Exit={diagnostic.Network.DemuxerExitReason}, Error={diagnostic.Network.LastNetworkError}");
        }
        finally
        {
            await player.StopAsync();
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
    /// 验证播放过程中切换媒体源不会残留旧管线。
    /// </summary>
    [Fact]
    public async Task Player_OpenAsync_WhilePlayingSwitchesSourceCleanly()
    {
        var ffmpegRoot = FFmpegFactAttribute.NativeRoot;
        var ffmpegExe = Path.Combine(ffmpegRoot, FFmpegLibraries.FFmpegExecutableName);
        if (!File.Exists(ffmpegExe))
        {
            return;
        }

        var tempDir = Path.Combine(Path.GetTempPath(), "FluxMediaTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var firstMediaPath = Path.Combine(tempDir, "session-switch-source-first.mp4");
        var secondMediaPath = Path.Combine(tempDir, "session-switch-source-second.mp4");
        var openedSourceGeneration = 0;
        var firstSourceFrame = new TaskCompletionSource<PlaybackVideoFrameEventArgs>();
        var secondSourceFrame = new TaskCompletionSource<PlaybackVideoFrameEventArgs>();

        await using var player = FluxEngine.CreatePlayer();
        try
        {
            await RunProcessAsync(
                ffmpegExe, $"-hide_banner -loglevel error -f lavfi -i testsrc=size=64x64:rate=8:duration=2 -an -pix_fmt yuv420p -y \"{firstMediaPath}\"");
            await RunProcessAsync(
                ffmpegExe, $"-hide_banner -loglevel error -f lavfi -i testsrc=size=96x72:rate=8:duration=2 -an -pix_fmt yuv420p -y \"{secondMediaPath}\"");

            player.VideoFrameReady += (_, args) =>
            {
                if (Volatile.Read(ref openedSourceGeneration) == 1)
                {
                    firstSourceFrame.TrySetResult(args);
                }
                else if (Volatile.Read(ref openedSourceGeneration) == 2)
                {
                    secondSourceFrame.TrySetResult(args);
                }
            };

            Interlocked.Exchange(ref openedSourceGeneration, 1);
            await player.OpenAsync(firstMediaPath, new PlaybackOptions
            {
                NativeLibraries = new NativeLibraryOptions
                {
                    FFmpegRootDirectory = ffmpegRoot
                }
            });

            var firstPlayResult = await player.PlayAsync();
            Assert.True(firstPlayResult.IsSuccess, firstPlayResult.Message);
            var firstFrame = (await firstSourceFrame.Task.WaitAsync(TimeSpan.FromSeconds(10))).Frame;
            Assert.Equal(64, firstFrame.Format.Width);
            Assert.Equal(64, firstFrame.Format.Height);

            Interlocked.Exchange(ref openedSourceGeneration, 2);
            await player.OpenAsync(secondMediaPath, new PlaybackOptions
            {
                NativeLibraries = new NativeLibraryOptions
                {
                    FFmpegRootDirectory = ffmpegRoot
                }
            });

            Assert.Equal(PlaybackState.Ready, player.State);
            var secondPlayResult = await player.PlayAsync();
            Assert.True(secondPlayResult.IsSuccess, secondPlayResult.Message);
            var secondFrame = (await secondSourceFrame.Task.WaitAsync(TimeSpan.FromSeconds(10))).Frame;
            var diagnostic = player.GetDiagnosticInfo();

            Assert.Equal(96, secondFrame.Format.Width);
            Assert.Equal(72, secondFrame.Format.Height);
            Assert.Equal(secondMediaPath, player.Source?.Uri);
            Assert.Equal(secondMediaPath, diagnostic.SourceUrl);
            Assert.True(diagnostic.Lifecycle.PlayCount >= 1);
            Assert.False(diagnostic.Health.IsStalled, diagnostic.Health.Reason);
        }
        finally
        {
            await player.StopAsync();
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
    /// 验证播放录制通过数据包封装生成输出文件。
    /// </summary>
    [Fact]
    public async Task Player_Recording_PlayingVideoWritesPacketRemuxOutput()
    {
        var ffmpegRoot = FFmpegFactAttribute.NativeRoot;
        var ffmpegExe = Path.Combine(ffmpegRoot, FFmpegLibraries.FFmpegExecutableName);
        if (!File.Exists(ffmpegExe))
        {
            return;
        }

        var tempDir = Path.Combine(Path.GetTempPath(), "FluxMediaTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var mediaPath = Path.Combine(tempDir, "session-recording-source.mp4");
        var outputPath = Path.Combine(tempDir, "session-recording-output.mp4");
        var firstFrame = new TaskCompletionSource<PlaybackVideoFrameEventArgs>();

        await using var player = FluxEngine.CreatePlayer();
        try
        {
            await RunProcessAsync(
                ffmpegExe, $"-hide_banner -loglevel error -f lavfi -i testsrc=size=64x64:rate=12:duration=3 -f lavfi -i sine=frequency=440:sample_rate=48000:duration=3 -shortest -pix_fmt yuv420p -movflags +faststart -y \"{mediaPath}\"");

            player.VideoFrameReady += (_, args) => firstFrame.TrySetResult(args);
            await player.OpenAsync(mediaPath, new PlaybackOptions
            {
                NativeLibraries = new NativeLibraryOptions
                {
                    FFmpegRootDirectory = ffmpegRoot
                }
            });

            var startRecordingResult = await player.StartRecordingAsync(outputPath);
            Assert.True(startRecordingResult.IsSuccess, startRecordingResult.Message);

            var playResult = await player.PlayAsync();
            Assert.True(playResult.IsSuccess, playResult.Message);
            await firstFrame.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await Task.Delay(700);

            var stopRecordingResult = await player.StopRecordingAsync();
            var recording = player.GetRecordingDiagnosticInfo();

            Assert.True(stopRecordingResult.IsSuccess, stopRecordingResult.Message);
            Assert.Equal(PlaybackRecordingState.Completed, recording.State);
            Assert.True(recording.ReceivedFrameCount > 0);
            Assert.True(recording.EncodedFrameCount > 0);
            Assert.True(File.Exists(outputPath));
            Assert.True(new FileInfo(outputPath).Length > 0);
        }
        finally
        {
            await player.StopAsync();
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
    /// 验证播放开始后录制能捕获已消费的数据包。
    /// </summary>
    [Fact]
    public async Task Player_Recording_AfterPlaybackStartedCapturesConsumedPackets()
    {
        var ffmpegRoot = FFmpegFactAttribute.NativeRoot;
        var ffmpegExe = Path.Combine(ffmpegRoot, FFmpegLibraries.FFmpegExecutableName);
        if (!File.Exists(ffmpegExe))
        {
            return;
        }

        var tempDir = Path.Combine(Path.GetTempPath(), "FluxMediaTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var mediaPath = Path.Combine(tempDir, "record-after-play-source.mp4");
        var outputPath = Path.Combine(tempDir, "record-after-play-output.mp4");
        var firstFrame = new TaskCompletionSource<PlaybackVideoFrameEventArgs>();

        await using var player = FluxEngine.CreatePlayer();
        try
        {
            await RunProcessAsync(
                ffmpegExe, $"-hide_banner -loglevel error -f lavfi -i testsrc=size=96x54:rate=15:duration=5 -f lavfi -i sine=frequency=550:sample_rate=48000:duration=5 -shortest -g 300 -keyint_min 300 -sc_threshold 0 -pix_fmt yuv420p -y \"{mediaPath}\"");

            player.VideoFrameReady += (_, args) => firstFrame.TrySetResult(args);
            Assert.True((await player.OpenAndPlayAsync(mediaPath, new PlaybackOptions
            {
                NativeLibraries = new NativeLibraryOptions { FFmpegRootDirectory = ffmpegRoot }
            })).IsSuccess);
            await firstFrame.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await Task.Delay(600);

            Assert.True((await player.StartRecordingAsync(outputPath)).IsSuccess);
            await Task.Delay(1200);
            var stop = await player.StopRecordingAsync();
            var recording = player.GetRecordingDiagnosticInfo();

            Assert.True(stop.IsSuccess, stop.Message);
            Assert.True(recording.EncodedFrameCount > 0);
            Assert.True(recording.OutputFileFinalized);
            Assert.True(new FileInfo(outputPath).Length > 0);
            await RunProcessAsync(ffmpegExe, $"-hide_banner -loglevel error -i \"{outputPath}\" -f null -");
        }
        finally
        {
            await player.StopAsync();
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
    /// 验证录制跨越前后跳转仍然有效。
    /// </summary>
    [Fact]
    public async Task Player_Recording_RemainsValidAcrossForwardAndBackwardSeek()
    {
        var ffmpegRoot = FFmpegFactAttribute.NativeRoot;
        var ffmpegExe = Path.Combine(ffmpegRoot, FFmpegLibraries.FFmpegExecutableName);
        if (!File.Exists(ffmpegExe))
        {
            return;
        }

        var tempDir = Path.Combine(Path.GetTempPath(), "FluxMediaTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var mediaPath = Path.Combine(tempDir, "recording-seek-source.mp4");
        var outputPath = Path.Combine(tempDir, "recording-seek-output.mkv");
        var firstFrame = new TaskCompletionSource<PlaybackVideoFrameEventArgs>();

        await using var player = FluxEngine.CreatePlayer();
        try
        {
            await RunProcessAsync(
                ffmpegExe, $"-hide_banner -loglevel error -f lavfi -i testsrc=size=96x54:rate=15:duration=8 -f lavfi -i sine=frequency=660:sample_rate=48000:duration=8 -shortest -g 15 -keyint_min 15 -sc_threshold 0 -pix_fmt yuv420p -y \"{mediaPath}\"");

            player.VideoFrameReady += (_, args) => firstFrame.TrySetResult(args);
            await player.OpenAsync(mediaPath, new PlaybackOptions
            {
                NativeLibraries = new NativeLibraryOptions { FFmpegRootDirectory = ffmpegRoot }
            });
            Assert.True((await player.StartRecordingAsync(outputPath)).IsSuccess);
            Assert.True((await player.PlayAsync()).IsSuccess);
            await firstFrame.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await Task.Delay(500);

            Assert.True((await player.SeekAsync(TimeSpan.FromSeconds(5))).IsSuccess);
            await Task.Delay(500);
            Assert.True((await player.SeekAsync(TimeSpan.FromSeconds(1))).IsSuccess);
            await Task.Delay(700);

            var stop = await player.StopRecordingAsync();
            var recording = player.GetRecordingDiagnosticInfo();
            Assert.True(stop.IsSuccess, stop.Message);
            Assert.Equal(PlaybackRecordingState.Completed, recording.State);
            Assert.True(recording.EncodedFrameCount > 0);
            Assert.True(recording.OutputFileFinalized);
            await RunProcessAsync(ffmpegExe, $"-hide_banner -loglevel error -i \"{outputPath}\" -f null -");
            var recordedDuration = await ProbeDurationAsync(
                Path.Combine(ffmpegRoot, FFmpegLibraries.FFprobeExecutableName), outputPath);
            Assert.InRange(recordedDuration, TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(4));
        }
        finally
        {
            await player.StopAsync();
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
    /// 验证管线重建后继续写入同一可解码文件。
    /// </summary>
    [Fact]
    public async Task Player_Recording_FullRestartKeepsSingleRemuxOutputDecodable()
    {
        var ffmpegRoot = FFmpegFactAttribute.NativeRoot;
        var ffmpegExe = Path.Combine(ffmpegRoot, FFmpegLibraries.FFmpegExecutableName);
        if (!File.Exists(ffmpegExe))
        {
            return;
        }

        var tempDir = Path.Combine(Path.GetTempPath(), "FluxMediaTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var mediaPath = Path.Combine(tempDir, "recording-full-restart-source.mp4");
        var outputPath = Path.Combine(tempDir, "recording-full-restart-output.mkv");
        var firstFrame = new TaskCompletionSource<PlaybackState>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var player = FluxEngine.CreatePlayer();
        try
        {
            await RunProcessAsync(
                ffmpegExe, $"-hide_banner -loglevel error -f lavfi -i testsrc=size=96x54:rate=15:duration=4 -f lavfi -i sine=frequency=720:sample_rate=48000:duration=4 -shortest -g 15 -pix_fmt yuv420p -y \"{mediaPath}\"");

            player.VideoFrameReady += (_, _) => firstFrame.TrySetResult(PlaybackState.Playing);

            await player.OpenAsync(mediaPath, new PlaybackOptions
            {
                NativeLibraries = new NativeLibraryOptions { FFmpegRootDirectory = ffmpegRoot }
            });
            Assert.True((await player.StartRecordingAsync(outputPath)).IsSuccess);
            Assert.True((await player.PlayAsync()).IsSuccess);
            await firstFrame.Task.WaitAsync(TimeSpan.FromSeconds(15));
            var demuxDeadline = DateTimeOffset.UtcNow.AddSeconds(6);
            while (player.GetDiagnosticInfo().Network.DemuxerState == "Running" && DateTimeOffset.UtcNow < demuxDeadline)
                await Task.Delay(10);
            Assert.Equal(PlaybackState.Playing, player.State);
            Assert.NotEqual("Running", player.GetDiagnosticInfo().Network.DemuxerState);

            var beforeRestart = player.GetRecordingDiagnosticInfo();
            Assert.True(beforeRestart.EncodedFrameCount > 0);
            Assert.True((await player.SeekAsync(TimeSpan.FromSeconds(1))).IsSuccess);
            Assert.Equal("FullRestart", player.GetDiagnosticInfo().LastSeek.Path);
            Assert.True((await player.ResumeAsync()).IsSuccess);
            await Task.Delay(1200);

            var stop = await player.StopRecordingAsync();
            var recording = player.GetRecordingDiagnosticInfo();
            Assert.True(stop.IsSuccess, stop.Message);
            Assert.Equal(PlaybackRecordingState.Completed, recording.State);
            Assert.True(recording.EncodedFrameCount > beforeRestart.EncodedFrameCount);
            Assert.True(recording.OutputFileFinalized);
            Assert.True(new FileInfo(outputPath).Length > 0);
            await RunProcessAsync(ffmpegExe, $"-hide_banner -loglevel error -i \"{outputPath}\" -f null -");
        }
        finally
        {
            await player.StopAsync();
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
    /// 验证持续播放和录制保留数据包直到自然结束。
    /// </summary>
    [Fact]
    public async Task Player_SustainedPlaybackAndRecording_PreservesPacketsUntilNaturalCompletion()
    {
        var ffmpegRoot = FFmpegFactAttribute.NativeRoot;
        var ffmpegExe = Path.Combine(ffmpegRoot, FFmpegLibraries.FFmpegExecutableName);
        if (!File.Exists(ffmpegExe))
        {
            return;
        }

        var tempDir = Path.Combine(Path.GetTempPath(), "FluxMediaTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var mediaPath = Path.Combine(tempDir, "sustained-source.mp4");
        var outputPath = Path.Combine(tempDir, "sustained-recording.mkv");
        var completed = new TaskCompletionSource<PlaybackState>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var player = FluxEngine.CreatePlayer();
        try
        {
            await RunProcessAsync(
                ffmpegExe, $"-hide_banner -loglevel error -f lavfi -i testsrc=size=96x54:rate=20:duration=6 -an -pix_fmt yuv420p -y \"{mediaPath}\"");

            player.StateChanged += (_, state) =>
            {
                if (state == PlaybackState.Completed)
                {
                    completed.TrySetResult(state);
                }
            };

            await player.OpenAsync(mediaPath, new PlaybackOptions
            {
                NativeLibraries = new NativeLibraryOptions { FFmpegRootDirectory = ffmpegRoot }
            });
            Assert.True((await player.StartRecordingAsync(outputPath)).IsSuccess);
            Assert.True((await player.PlayAsync()).IsSuccess);
            await completed.Task.WaitAsync(TimeSpan.FromSeconds(15));

            var stopRecording = await player.StopRecordingAsync();
            var playback = player.GetDiagnosticInfo();
            var recording = player.GetRecordingDiagnosticInfo();
            Assert.True(stopRecording.IsSuccess, stopRecording.Message);
            Assert.Equal(PlaybackState.Completed, player.State);
            Assert.True(playback.DecodedFrameCount >= 100, $"Decoded={playback.DecodedFrameCount}");
            Assert.True(recording.EncodedFrameCount >= 100, $"Recorded={recording.EncodedFrameCount}");
            Assert.Equal(PlaybackRecordingState.Completed, recording.State);
            await RunProcessAsync(ffmpegExe, $"-hide_banner -loglevel error -i \"{outputPath}\" -f null -");
        }
        finally
        {
            await player.StopAsync();
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
    /// 验证无效录制路径不会中断正常播放。
    /// </summary>
    [Fact]
    public async Task Player_Recording_InvalidOutputPathFailsWithoutBreakingPlayback()
    {
        var ffmpegRoot = FFmpegFactAttribute.NativeRoot;
        var ffmpegExe = Path.Combine(ffmpegRoot, FFmpegLibraries.FFmpegExecutableName);
        if (!File.Exists(ffmpegExe))
        {
            return;
        }

        var tempDir = Path.Combine(Path.GetTempPath(), "FluxMediaTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var mediaPath = Path.Combine(tempDir, "session-recording-invalid-output-source.mp4");
        var outputPath = Path.Combine(tempDir, "missing", "recording.mp4");
        var firstFrame = new TaskCompletionSource<PlaybackVideoFrameEventArgs>();

        await using var player = FluxEngine.CreatePlayer();
        try
        {
            await RunProcessAsync(
                ffmpegExe, $"-hide_banner -loglevel error -f lavfi -i testsrc=size=64x64:rate=12:duration=2 -an -pix_fmt yuv420p -y \"{mediaPath}\"");

            player.VideoFrameReady += (_, args) => firstFrame.TrySetResult(args);
            await player.OpenAsync(mediaPath, new PlaybackOptions
            {
                NativeLibraries = new NativeLibraryOptions
                {
                    FFmpegRootDirectory = ffmpegRoot
                }
            });

            var playResult = await player.PlayAsync();
            Assert.True(playResult.IsSuccess, playResult.Message);
            await firstFrame.Task.WaitAsync(TimeSpan.FromSeconds(10));

            var recordingResult = await player.StartRecordingAsync(new PlaybackRecordingOptions
            {
                OutputPath = outputPath,
                CreateOutputDirectory = false
            });

            var diagnostic = player.GetDiagnosticInfo();
            var recording = player.GetRecordingDiagnosticInfo();
            Assert.False(recordingResult.IsSuccess);
            Assert.Equal(PlaybackError.RecordingFailed, recordingResult.Error);
            Assert.Equal(PlaybackRecordingState.Failed, recording.State);
            Assert.False(string.IsNullOrWhiteSpace(recording.LastError));
            Assert.Equal(PlaybackState.Playing, player.State);
            Assert.False(diagnostic.Health.IsStalled, diagnostic.Health.Reason);

            await player.StopAsync();
            Assert.Equal(PlaybackState.Stopped, player.State);
        }
        finally
        {
            await player.StopAsync();
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
    /// 使用媒体探测工具读取输出文件时长。
    /// </summary>
    private static async Task<TimeSpan> ProbeDurationAsync(string ffprobePath, string mediaPath)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = ffprobePath,
            Arguments = $"-v error -show_entries format=duration -of default=noprint_wrappers=1:nokey=1 \"{mediaPath}\"",
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        }) ?? throw new InvalidOperationException($"Failed to start {ffprobePath}.");

        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        if (process.ExitCode != 0 ||
            !double.TryParse(
                stdout.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var seconds))
        {
            throw new InvalidOperationException($"ffprobe failed.\n{stdout}\n{stderr}");
        }

        return TimeSpan.FromSeconds(seconds);
    }

    /// <summary>
    /// 本地 HTTP 文件服务器。
    /// </summary>
    private sealed class LocalHttpFileServer : IDisposable
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
        /// 取消源。
        /// </summary>
        private readonly CancellationTokenSource _cts = new();

        /// <summary>
        /// 服务任务。
        /// </summary>
        private readonly Task _serveTask;

        /// <summary>
        /// 停滞之后字节。
        /// </summary>
        private readonly long? _stallAfterBytes;

        /// <summary>
        /// 停滞延迟。
        /// </summary>
        private readonly TimeSpan _stallDelay;

        /// <summary>
        /// 已接受连接数量。
        /// </summary>
        private int _acceptedConnectionCount;

        /// <summary>
        /// 初始化本地 HTTP 文件服务器所需的状态和资源。
        /// </summary>
        private LocalHttpFileServer(TcpListener listener, string filePath, long? stallAfterBytes, TimeSpan? stallDelay)
        {
            _listener = listener;
            _filePath = filePath;
            _stallAfterBytes = stallAfterBytes;
            _stallDelay = stallDelay ?? TimeSpan.Zero;
            var port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            Url = $"http://127.0.0.1:{port}/media.mp4";
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
        public static LocalHttpFileServer Start(string filePath, long? stallAfterBytes = null, TimeSpan? stallDelay = null)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return new LocalHttpFileServer(listener, filePath, stallAfterBytes, stallDelay);
        }

        /// <summary>
        /// 释放当前对象持有的资源。
        /// </summary>
        public void Dispose()
        {
            _cts.Cancel();
            _listener.Stop();
            try
            {
                _serveTask.Wait(TimeSpan.FromSeconds(2));
            }
            catch
            {
            }

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

                Interlocked.Increment(ref _acceptedConnectionCount);
                _ = Task.Run(() => HandleClientAsync(client, _cts.Token));
            }
        }

        /// <summary>
        /// 处理单个 HTTP 客户端的媒体读取请求。
        /// </summary>
        private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
        {
            using var _ = client;
            using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
            var requestLine = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(requestLine))
            {
                return;
            }

            var rangeStart = 0L;
            string? line;
            while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false)))
            {
                if (line.StartsWith("Range:", StringComparison.OrdinalIgnoreCase))
                {
                    var marker = "bytes=";
                    var index = line.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
                    if (index >= 0)
                    {
                        var range = line[(index + marker.Length)..].Split('-', 2)[0];
                        long.TryParse(range, out rangeStart);
                    }
                }
            }

            var fileLength = new FileInfo(_filePath).Length;
            rangeStart = Math.Clamp(rangeStart, 0, Math.Max(0, fileLength - 1));
            var contentLength = fileLength - rangeStart;
            var partial = rangeStart > 0;
            var status = partial ? "206 Partial Content" : "200 OK";
            var header =
                $"HTTP/1.1 {status}\r\n" +
                "Content-Type: video/mp4\r\n" +
                "Accept-Ranges: bytes\r\n" +
                $"Content-Length: {contentLength}\r\n" +
                (partial ? $"Content-Range: bytes {rangeStart}-{fileLength - 1}/{fileLength}\r\n" : string.Empty) +
                "Connection: close\r\n\r\n";
            var headerBytes = Encoding.ASCII.GetBytes(header);
            await stream.WriteAsync(headerBytes, cancellationToken).ConfigureAwait(false);

            if (requestLine.StartsWith("HEAD ", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            await using var file = File.OpenRead(_filePath);
            file.Position = rangeStart;
            if (_stallAfterBytes is null || rangeStart > 0)
            {
                await file.CopyToAsync(stream, cancellationToken).ConfigureAwait(false);
                return;
            }

            var remainingBeforeStall = Math.Min(_stallAfterBytes.Value, contentLength);
            var buffer = new byte[16 * 1024];
            while (remainingBeforeStall > 0)
            {
                var read = await file.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remainingBeforeStall)), cancellationToken).ConfigureAwait(false);
                if (read <= 0)
                {
                    return;
                }

                await stream.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                remainingBeforeStall -= read;
            }

            await Task.Delay(_stallDelay, cancellationToken).ConfigureAwait(false);
        }
    }
}
