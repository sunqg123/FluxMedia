using System.Collections.Concurrent;
using FluxMedia.Core.Configuration;
using FluxMedia.Core.Logging;
using FluxMedia.Core.Models;
using FluxMedia.Core.State;
using FluxMedia.Playback;

namespace FluxMedia.Tests;

/// <summary>
/// 验证静态日志入口、并发切换和运行时所有权。
/// </summary>
public sealed class StaticLoggingTests : IDisposable
{
    /// <summary>
    /// 每个测试从关闭的全局日志状态开始。
    /// </summary>
    public StaticLoggingTests()
    {
        FluxEngine.Shutdown();
        FluxEngine.Shutdown(); FluxMediaLog.Shutdown();
    }

    /// <summary>
    /// 清理测试创建的共享运行时和日志输出。
    /// </summary>
    public void Dispose()
    {
        FluxEngine.Shutdown();
        FluxEngine.Shutdown(); FluxMediaLog.Shutdown();
    }

    /// <summary>
    /// 关闭日志时不创建目录，写入与刷新均为空操作。
    /// </summary>
    [Fact]
    public void DisabledLogging_DoesNotCreateDirectory()
    {
        var directory = NewDirectory();
        Assert.False(FluxMediaLog.IsEnabled);
        FluxMediaLog.Configure(new LoggingOptions { Directory = directory, IsEnabled = false });
        FluxMediaLog.Error("Test", "不应写入");
        FluxMediaLog.Flush();
        Assert.False(Directory.Exists(directory));
        Assert.Null(FluxMediaLog.CurrentLogFilePath);
    }

    /// <summary>
    /// None 级别不创建文件，恢复级别后可直接开始输出。
    /// </summary>
    [Fact]
    public void NoneLevel_CreatesNoTargetUntilEnabledLevelIsSelected()
    {
        var directory = NewDirectory();
        FluxMediaLog.Configure(new LoggingOptions { Directory = directory, MinimumLevel = LogLevel.None });
        FluxMediaLog.Info("Test", "不输出");
        Assert.False(Directory.Exists(directory));
        FluxMediaLog.MinimumLevel = LogLevel.Information;
        FluxMediaLog.Info("Test", "Event=EnabledAgain");
        var file = FluxMediaLog.CurrentLogFilePath!;
        FluxMediaLog.Shutdown();
        Assert.Contains("Event=EnabledAgain", File.ReadAllText(file));
    }

    /// <summary>
    /// 文件输出遵循动态级别及开关，关闭时排空已接收记录。
    /// </summary>
    [Fact]
    public void FileOutput_HonorsRuntimeLevelChangesAndDrainsOnShutdown()
    {
        FluxMediaLog.Configure(new LoggingOptions { Directory = NewDirectory(), MinimumLevel = LogLevel.Information });
        var id = Guid.NewGuid();
        FluxMediaLog.Debug("Test", "DebugHidden");
        FluxMediaLog.Info("Test", "Event=InfoVisible", id);
        FluxMediaLog.MinimumLevel = LogLevel.Debug;
        FluxMediaLog.Debug("Test", "Event=DebugVisible");
        FluxMediaLog.IsEnabled = false;
        FluxMediaLog.Error("Test", "ErrorHidden");
        var file = FluxMediaLog.CurrentLogFilePath!;
        FluxMediaLog.Shutdown();
        var content = File.ReadAllText(file);
        Assert.Contains("InfoVisible", content);
        Assert.Contains("DebugVisible", content);
        Assert.Contains($"SessionId={id}", content);
        Assert.DoesNotContain("DebugHidden", content);
        Assert.DoesNotContain("ErrorHidden", content);
        Assert.Null(FluxMediaLog.LogDirectory);
    }

    /// <summary>
    /// 委托输出能保留原始级别、会话标识和异常对象。
    /// </summary>
    [Fact]
    public void CallbackOutput_PreservesContextAndFiltersLevels()
    {
        var entries = new ConcurrentQueue<(LogLevel Level, string Category, Guid Id, string Message, Exception? Error)>();
        FluxMediaLog.SetSink((level, category, id, message, error) => entries.Enqueue((level, category, id, message, error)));
        FluxMediaLog.IsEnabled = true;
        FluxMediaLog.MinimumLevel = LogLevel.Warning;
        var id = Guid.NewGuid();
        var error = new InvalidOperationException("原始异常");
        FluxMediaLog.Info("Test", "过滤的信息");
        FluxMediaLog.Error("Playback", "失败", error, id);
        var entry = Assert.Single(entries);
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Equal("Playback", entry.Category);
        Assert.Equal(id, entry.Id);
        Assert.Same(error, entry.Error);
    }

    /// <summary>
    /// 输出委托抛异常不阻止媒体状态正常切换。
    /// </summary>
    [Fact]
    public async Task ThrowingCallback_DoesNotRejectStateTransition()
    {
        FluxMediaLog.SetSink((_, _, _, _) => throw new InvalidOperationException("输出故障"));
        FluxMediaLog.MinimumLevel = LogLevel.Debug;
        FluxMediaLog.IsEnabled = true;
        await using var component = new ProbeStateMachine();
        await component.MoveAsync();
        Assert.Equal(ProbeState.Running, component.State);
    }

    /// <summary>
    /// 已创建的播放器也会使用后来设置的静态日志目标。
    /// </summary>
    [Fact]
    public async Task ExistingSession_UsesNewStaticConfiguration()
    {
        await using var session = new PlaybackSession();
        var entries = new ConcurrentQueue<string>();
        FluxMediaLog.SetSink((_, _, message, _) => entries.Enqueue(message));
        FluxMediaLog.IsEnabled = true;
        var missing = Path.Combine(NewDirectory(), "missing.mp4");
        await Assert.ThrowsAsync<PlaybackException>(() => session.OpenAsync(new PlaybackOpenRequest(MediaSource.FromFile(missing))).AsTask());
        Assert.Contains(entries, text => text.Contains("媒体文件不存在", StringComparison.Ordinal));
    }

    /// <summary>
    /// 回调内可以切换目标，递归写入会被隔离而不是死锁。
    /// </summary>
    [Fact]
    public void ReentrantCallback_CanReplaceTargetWithoutRecursion()
    {
        var first = 0;
        var second = 0;
        FluxMediaLog.SetSink((_, _, _, _) =>
        {
            first++;
            FluxMediaLog.Info("Test", "递归记录");
            FluxMediaLog.SetSink((_, _, _, _) => second++);
        });
        FluxMediaLog.IsEnabled = true;
        FluxMediaLog.Info("Test", "第一次");
        FluxMediaLog.Info("Test", "第二次");
        Assert.Equal(1, first);
        Assert.Equal(1, second);
    }

    /// <summary>
    /// 回调内关闭日志不会等待自身结束而死锁。
    /// </summary>
    [Fact]
    public void Callback_CanShutdownLogging()
    {
        FluxMediaLog.SetSink((_, _, _, _) => FluxMediaLog.Shutdown());
        FluxMediaLog.IsEnabled = true;
        FluxMediaLog.Info("Test", "关闭");
        Assert.False(FluxMediaLog.IsEnabled);
    }

    /// <summary>
    /// 并发切换文件时，未满载的已接收记录最终全部写入。
    /// </summary>
    [Fact]
    public async Task ConcurrentFileSwitches_DoNotLoseAcceptedRecords()
    {
        var directory = NewDirectory();
        FluxMediaLog.Configure(new LoggingOptions { Directory = Path.Combine(directory, "initial") });
        using var start = new ManualResetEventSlim();
        var writes = Task.Run(() =>
        {
            start.Wait();
            Parallel.For(0, 1000, index => FluxMediaLog.Info("Race", $"Event=RaceWrite Index={index}"));
        });
        var switches = Task.Run(() =>
        {
            start.Wait();
            for (var index = 0; index < 10; index++)
                FluxMediaLog.Configure(new LoggingOptions { Directory = Path.Combine(directory, index.ToString()) });
        });
        start.Set();
        await Task.WhenAll(writes, switches).WaitAsync(TimeSpan.FromSeconds(15));
        FluxMediaLog.Shutdown();
        var lines = Directory.GetFiles(directory, "*.txt", SearchOption.AllDirectories).SelectMany(File.ReadAllLines);
        Assert.Equal(1000, lines.Count(line => line.Contains("Event=RaceWrite", StringComparison.Ordinal)));
    }

    /// <summary>
    /// 默认引擎启动不覆盖宿主独立配置的静态输出。
    /// </summary>
    [Fact]
    public void DefaultEngine_DoesNotOverwriteHostCallback()
    {
        var entries = new ConcurrentQueue<string>();
        FluxMediaLog.SetSink((_, _, message, _) => entries.Enqueue(message));
        FluxMediaLog.IsEnabled = true;
        FluxEngine.Start();
        FluxEngine.Shutdown();
        FluxMediaLog.Info("Host", "HostAfterShutdown");
        Assert.True(FluxMediaLog.IsEnabled);
        Assert.Contains("HostAfterShutdown", entries);
    }

    /// <summary>
    /// 旧运行时关闭时，不释放宿主后来替换的新输出。
    /// </summary>
    [Fact]
    public void OldRuntime_DoesNotShutdownReplacementTarget()
    {
        FluxEngine.Start(new FluxEngineOptions { EnableLogging = true, LogDirectory = NewDirectory() });
        var entries = new ConcurrentQueue<string>();
        FluxMediaLog.SetSink((_, _, message, _) => entries.Enqueue(message));
        FluxEngine.Shutdown();
        FluxMediaLog.Info("Host", "ReplacementStillEnabled");
        Assert.True(FluxMediaLog.IsEnabled);
        Assert.Contains("ReplacementStillEnabled", entries);
    }

    /// <summary>
    /// 新公开 API 仅暴露静态入口，不再暴露日志注入接口。
    /// </summary>
    [Fact]
    public void PublicSurface_ContainsStaticFacadeWithoutLoggerInjection()
    {
        var types = typeof(Player).Assembly.GetExportedTypes();
        Assert.True(typeof(FluxMediaLog).IsAbstract && typeof(FluxMediaLog).IsSealed);
        Assert.DoesNotContain(types, type => type.Name is "IFluxMediaLogger" or "FluxMediaLoggerProvider");
        Assert.DoesNotContain(types.SelectMany(type => type.GetConstructors()).SelectMany(ctor => ctor.GetParameters()), parameter => parameter.Name == "logger");
        Assert.DoesNotContain(types.SelectMany(type => type.GetMethods()).SelectMany(method => method.GetParameters()), parameter => parameter.Name == "logger");
        Assert.Null(typeof(Player).Assembly.GetType("FluxMedia.Runtime.IFluxMediaRuntime"));
    }

    /// <summary>
    /// 为文件输出测试分配独立目录，创建由日志入口负责。
    /// </summary>
    private static string NewDirectory() => Path.Combine(Path.GetTempPath(), "FluxMediaStaticLogTests", Guid.NewGuid().ToString("N"));

    /// <summary>
    /// 用于观察日志失败是否影响状态切换的最小组件。
    /// </summary>
    private sealed class ProbeStateMachine : AsyncMediaStateMachine<ProbeState>
    {
        /// <summary>
        /// 创建不依赖日志器参数的测试组件。
        /// </summary>
        public ProbeStateMachine() : base(ProbeState.Created, "StaticLogProbe") { }

        /// <summary>
        /// 执行一次会记录调试日志的状态切换。
        /// </summary>
        public ValueTask MoveAsync() => TransitionAsync(ProbeState.Running);
    }

    /// <summary>
    /// 日志隔离测试使用的组件状态。
    /// </summary>
    private enum ProbeState
    {
        /// <summary>
        /// 已创建，尚未执行操作。
        /// </summary>
        Created,

        /// <summary>
        /// 已成功切换到运行状态。
        /// </summary>
        Running
    }
}
