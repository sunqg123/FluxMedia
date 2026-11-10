using FluxMedia.Playback;
using FluxMedia.Core.Configuration;
using FluxMedia.Core.Logging;
using FluxMedia.FFmpeg.Native;

namespace FluxMedia.Tests;

/// <summary>
/// 文件日志测试。
/// </summary>
public sealed class FileLoggingTests
{
    /// <summary>
    /// 显式目录不完整时使用环境变量中的完整目录。
    /// </summary>
    [Fact]
    public void NativeLibraryLoader_UsesEnvironmentWhenExplicitDirectoryIsIncomplete()
    {
        var directory = Path.Combine(Path.GetTempPath(), "FluxMediaNativePath", Guid.NewGuid().ToString("N"));
        var explicitRoot = Path.Combine(directory, "explicit");
        var environmentRoot = Path.Combine(directory, "environment");
        var previous = Environment.GetEnvironmentVariable("FLUXMEDIA_FFMPEG_ROOT");
        Directory.CreateDirectory(explicitRoot);
        Directory.CreateDirectory(environmentRoot);
        try
        {
            foreach (var name in FFmpegLibraries.RequiredFileNames)
                File.WriteAllBytes(Path.Combine(environmentRoot, name), Array.Empty<byte>());
            Environment.SetEnvironmentVariable("FLUXMEDIA_FFMPEG_ROOT", environmentRoot);
            var loader = new NativeLibraryLoader(explicitRoot);
            Assert.Equal(environmentRoot, loader.ResolveDirectory());
        }
        finally
        {
            Environment.SetEnvironmentVariable("FLUXMEDIA_FFMPEG_ROOT", previous);
            Directory.Delete(directory, true);
        }
    }

    /// <summary>
    /// 验证结构化日志写入文件且可主动刷新。
    /// </summary>
    [Fact]
    public void FileLogger_WritesStructuredTxtFileAndFlushesContent()
    {
        var directory = Path.Combine(Path.GetTempPath(), "FluxMediaTests", Guid.NewGuid().ToString("N"));
        var instanceId = Guid.NewGuid();

        using var logger = new FileFluxMediaLogSink(new LoggingOptions
        {
            IsEnabled = true,
            MinimumLevel = LogLevel.Debug,
            Directory = directory,
            RollingMode = LogFileRollingMode.PerRun,
            FilePrefix = "FluxMediaTest"
        });

        logger.Write(LogLevel.Information, "PlaybackTest", instanceId, "OpenMedia。SourceKind=NetworkStream, Url=http://127.0.0.1/video.mp4, OpenElapsed=12ms");
        logger.Write(LogLevel.Information, "PlaybackTest", instanceId, "Event=PlaybackStarted State=Playing Position=00:00:01");
        logger.Write(LogLevel.Error, "PlaybackTest", instanceId, "OpenFailed。FFmpegErrorCode=-1, FFmpegErrorText=InvalidData", new InvalidOperationException("测试异常"));
        logger.Flush();

        var file = Directory.GetFiles(directory, "*.txt").Single();
        var content = File.ReadAllText(file);

        Assert.EndsWith(".txt", file, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[Info] [PlaybackTest]", content, StringComparison.Ordinal);
        Assert.Contains($"SessionId={instanceId}", content, StringComparison.Ordinal);
        Assert.Contains("ThreadId=", content, StringComparison.Ordinal);
        Assert.Contains("Event=OpenMedia", content, StringComparison.Ordinal);
        Assert.Contains("SourceKind=NetworkStream", content, StringComparison.Ordinal);
        Assert.Contains("Url=http://127.0.0.1/video.mp4", content, StringComparison.Ordinal);
        Assert.Contains("OpenElapsed=12ms", content, StringComparison.Ordinal);
        Assert.Contains("Event=PlaybackStarted State=Playing Position=00:00:01", content, StringComparison.Ordinal);
        Assert.DoesNotContain("Event=Event=", content, StringComparison.Ordinal);
        Assert.Contains("[Error] [PlaybackTest]", content, StringComparison.Ordinal);
        Assert.Contains("Event=OpenFailed", content, StringComparison.Ordinal);
        Assert.Contains("FFmpegErrorCode=-1", content, StringComparison.Ordinal);
        Assert.Contains("FFmpegErrorText=InvalidData", content, StringComparison.Ordinal);
        Assert.Contains("ExceptionType=System.InvalidOperationException", content, StringComparison.Ordinal);
        Assert.Contains("ExceptionMessage=\"测试异常\"", content, StringComparison.Ordinal);
        Assert.Contains("StackTrace:", content, StringComparison.Ordinal);
    }

    /// <summary>
    /// 验证默认日志目录和每日文件名保持兼容。
    /// </summary>
    [Fact]
    public void FileLogger_DefaultStyle_MatchesLegacyLogsDirectoryAndDailyTxtName()
    {
        var directory = Path.Combine(Path.GetTempPath(), "FluxMediaTests", Guid.NewGuid().ToString("N"), "Logs");
        using var logger = new FileFluxMediaLogSink(new LoggingOptions
        {
            IsEnabled = true,
            MinimumLevel = LogLevel.Debug,
            Directory = directory
        });

        logger.Write(LogLevel.Debug, "LegacyStyle", Guid.Empty, "FrameStats。ReadPackets=1, DecodedFrames=1, PushedFrames=1");
        logger.Flush();

        var expectedFile = Path.Combine(directory, $"{DateTimeOffset.Now:yyyyMMdd}.txt");
        Assert.True(File.Exists(expectedFile), expectedFile);
        var content = File.ReadAllText(expectedFile);
        Assert.Contains("[Debug] [LegacyStyle]", content, StringComparison.Ordinal);
        Assert.Contains("Event=FrameStats", content, StringComparison.Ordinal);
        Assert.Contains("ReadPackets=1", content, StringComparison.Ordinal);
        Assert.Contains("DecodedFrames=1", content, StringComparison.Ordinal);
        Assert.Contains("PushedFrames=1", content, StringComparison.Ordinal);
    }

    /// <summary>
    /// 引擎重复启动沿用已创建的日志输出。
    /// </summary>
    [FFmpegFact]
    public void Engine_StartIsIdempotentAndWritesFile()
    {
        FluxEngine.Shutdown(); FluxMediaLog.Shutdown();
        var message = "Event=EngineSmoke Token=" + Guid.NewGuid().ToString("N");
        try
        {
            FluxEngine.Start(new FluxEngineOptions { FFmpegPath = FFmpegFactAttribute.NativeRoot, EnableLogging = true });
            var path = FluxEngine.CurrentLogFilePath;
            FluxEngine.Start();
            Assert.Throws<InvalidOperationException>(() =>
                FluxEngine.Start(new FluxEngineOptions { FFmpegPath = FFmpegFactAttribute.NativeRoot, EnableLogging = false }));
            FluxMediaLog.Info("EngineTest", message);
            FluxMediaLog.Flush();
            Assert.Equal(path, FluxEngine.CurrentLogFilePath);
            Assert.True(FluxEngine.IsStarted);
            Assert.Contains(message, File.ReadAllText(path!));
        }
        finally { FluxEngine.Shutdown(); }
    }

    /// <summary>
    /// 引擎只关闭自己创建的文件输出。
    /// </summary>
    [FFmpegFact]
    public void Engine_ShutdownClosesOwnedLogging()
    {
        FluxEngine.Shutdown(); FluxMediaLog.Shutdown();
        var directory = Path.Combine(AppContext.BaseDirectory, "Logs");
        FluxEngine.Start(new FluxEngineOptions { FFmpegPath = FFmpegFactAttribute.NativeRoot, EnableLogging = true });
        Assert.True(FluxMediaLog.IsEnabled);
        Assert.Equal(directory, FluxMediaLog.LogDirectory);
        FluxEngine.Shutdown();
        Assert.False(FluxMediaLog.IsEnabled);
        Assert.Null(FluxMediaLog.CurrentLogFilePath);
    }

    /// <summary>
    /// 完整的显式目录优先于环境变量。
    /// </summary>
    [Fact]
    public void NativeLibraryLoader_UsesExplicitDirectoryFirst()
    {
        var directory = Path.Combine(Path.GetTempPath(), "FluxMediaNativePath", Guid.NewGuid().ToString("N"));
        var previous = Environment.GetEnvironmentVariable("FLUXMEDIA_FFMPEG_ROOT");
        Directory.CreateDirectory(directory);
        try
        {
            foreach (var name in FFmpegLibraries.RequiredFileNames)
                File.WriteAllBytes(Path.Combine(directory, name), Array.Empty<byte>());
            Environment.SetEnvironmentVariable("FLUXMEDIA_FFMPEG_ROOT", Path.Combine(directory, "unused"));
            var loader = new NativeLibraryLoader(directory);
            Assert.Equal(directory, loader.ResolveDirectory());
        }
        finally
        {
            Environment.SetEnvironmentVariable("FLUXMEDIA_FFMPEG_ROOT", previous);
            Directory.Delete(directory, true);
        }
    }

}
