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
    /// 首个库文件损坏时继续搜索后续有效路径。
    /// </summary>
    [FFmpegFact]
    public void NativeLibraryLoader_TriesOtherPathsBeforeThrowing()
    {
        var directory = Path.Combine(Path.GetTempPath(), "FluxMediaNativeSearch", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, "kernel32.dll"), "invalid library");
            var system = Environment.GetFolderPath(Environment.SpecialFolder.System);
            var result = new NativeLibraryLoader(new NativeLibraryOptions
            {
                FFmpegRootDirectory = directory,
                AdditionalSearchDirectories = new[] { system },
                ThrowOnMissingLibrary = true
            }).TryLoad(new NativeLibraryDescriptor("kernel32", new[] { "kernel32.dll" }));
            Assert.True(result.IsLoaded);
            Assert.Equal(Path.Combine(system, "kernel32.dll"), result.LoadedPath);
        }
        finally { Directory.Delete(directory, true); }
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
    [Fact]
    public void Engine_StartIsIdempotentAndWritesFile()
    {
        FluxEngine.Shutdown(); FluxMediaLog.Shutdown();
        var directory = Path.Combine(Path.GetTempPath(), "FluxMediaTests", Guid.NewGuid().ToString("N"));
        try
        {
            FluxEngine.Start(new FluxEngineOptions { EnableLogging = true, LogDirectory = directory, LogLevel = LogLevel.Debug });
            var path = FluxEngine.CurrentLogFilePath;
            FluxEngine.Start(new FluxEngineOptions { LogDirectory = directory + "-unused" });
            FluxMediaLog.Info("EngineTest", "Event=EngineSmoke");
            FluxMediaLog.Flush();
            Assert.Equal(path, FluxEngine.CurrentLogFilePath);
            Assert.True(FluxEngine.IsStarted);
            Assert.Contains("Event=EngineSmoke", File.ReadAllText(path!));
        }
        finally { FluxEngine.Shutdown(); }
    }

    /// <summary>
    /// 引擎只关闭自己创建的文件输出。
    /// </summary>
    [Fact]
    public void Engine_ShutdownClosesOwnedLogging()
    {
        FluxEngine.Shutdown(); FluxMediaLog.Shutdown();
        var directory = Path.Combine(Path.GetTempPath(), "FluxMediaTests", Guid.NewGuid().ToString("N"));
        FluxEngine.Start(new FluxEngineOptions { EnableLogging = true, LogDirectory = directory });
        Assert.True(FluxMediaLog.IsEnabled);
        Assert.Equal(directory, FluxMediaLog.LogDirectory);
        FluxEngine.Shutdown();
        Assert.False(FluxMediaLog.IsEnabled);
        Assert.Null(FluxMediaLog.CurrentLogFilePath);
    }

    /// <summary>
    /// 原生库加载器使用显式配置，不读取隐藏的运行时状态。
    /// </summary>
    [Fact]
    public void NativeLibraryLoader_UsesOnlyExplicitConfiguration()
    {
        var root = Path.Combine(Path.GetTempPath(), "FluxMediaTests", "native");
        var extra = Path.Combine(Path.GetTempPath(), "FluxMediaTests", "extra");
        var directories = new NativeLibraryLoader(new NativeLibraryOptions
        {
            FFmpegRootDirectory = root,
            AdditionalSearchDirectories = new[] { extra }
        }).BuildSearchDirectories();
        Assert.Equal(root, directories[0]);
        Assert.Contains(extra, directories);
    }

    /// <summary>
    /// 唯一配置合并入口保留显式路径并继承引擎的附加路径。
    /// </summary>
    [Fact]
    public void EngineOptions_MergeKeepsExplicitRootAndCombinesSearchPaths()
    {
        var defaults = new FluxEngineOptions
        {
            FFmpegPath = "engine-native",
            FFmpegSearchPaths = new[] { "engine-extra" },
            DefaultOpenTimeout = TimeSpan.FromSeconds(9)
        };
        var merged = defaults.Merge(new PlaybackOptions
        {
            NativeLibraries = new NativeLibraryOptions { FFmpegRootDirectory = "player-native", AdditionalSearchDirectories = new[] { "player-extra" } }
        });
        Assert.Equal("player-native", merged.NativeLibraries.FFmpegRootDirectory);
        Assert.Equal(new[] { "engine-extra", "player-extra" }, merged.NativeLibraries.AdditionalSearchDirectories);
        Assert.Equal(TimeSpan.FromSeconds(9), defaults.Merge(null).OpenTimeout);
    }
}
