using FluxMedia.Core.Configuration;
using FluxMedia.Core.Logging;
using FluxMedia.FFmpeg.Native;

namespace FluxMedia.Playback;

/// <summary>
/// 初始化共享环境并统一管理播放器。
/// </summary>
public static class FluxEngine
{
    /// <summary>
    /// 保护引擎配置和播放器登记。
    /// </summary>
    private static readonly object SyncRoot = new();
    /// <summary>
    /// 引擎拥有的活动播放器。
    /// </summary>
    private static readonly Dictionary<Guid, Player> ActivePlayers = new();
    /// <summary>
    /// 当前启动配置；未启动时为空。
    /// </summary>
    private static FluxEngineOptions? _options;
    /// <summary>
    /// 引擎创建的日志输出版本。
    /// </summary>
    private static long? _logGeneration;
    /// <summary>
    /// 并发关闭共享的完成任务。
    /// </summary>
    private static Task? _shutdownTask;

    /// <summary>
    /// 是否已启动且未进入关闭流程。
    /// </summary>
    public static bool IsStarted
    {
        get { lock (SyncRoot) return _options is not null && _shutdownTask is null; }
    }
    /// <summary>
    /// 当前日志目录。
    /// </summary>
    public static string? LogDirectory => FluxMediaLog.LogDirectory;
    /// <summary>
    /// 当前日志文件。
    /// </summary>
    public static string? CurrentLogFilePath => FluxMediaLog.CurrentLogFilePath;
    /// <summary>
    /// 当前由引擎管理的播放器快照。
    /// </summary>
    public static IReadOnlyCollection<Player> Players
    {
        get { lock (SyncRoot) return Array.AsReadOnly(ActivePlayers.Values.ToArray()); }
    }

    /// <summary>
    /// 初始化原生环境；重复启动沿用当前配置。
    /// </summary>
    public static void Start(FluxEngineOptions? options = null)
    {
        lock (SyncRoot)
        {
            if (_shutdownTask is not null) throw new InvalidOperationException("引擎正在关闭，请等待 ShutdownAsync 完成。");
            if (_options is not null) return;
            var actual = options ?? new FluxEngineOptions();
            actual = actual with { FFmpegSearchPaths = actual.FFmpegSearchPaths.ToArray() };
            long? generation = null;
            try
            {
                if (actual.EnableLogging && actual.LogLevel != LogLevel.None)
                    generation = FluxMediaLog.ConfigureForEngine(new LoggingOptions
                    {
                        IsEnabled = true,
                        Directory = actual.LogDirectory,
                        MinimumLevel = actual.LogLevel
                    });
                FFmpegNativeRuntime.Initialize(actual.NativeOptions);
                _logGeneration = generation;
                _options = actual;
            }
            catch
            {
                if (generation.HasValue) FluxMediaLog.ShutdownIfCurrent(generation.Value);
                throw;
            }
        }
    }

    /// <summary>
    /// 创建独立播放器；配置和登记在同一次锁内完成。
    /// </summary>
    public static Player CreatePlayer(PlaybackOptions? options = null)
    {
        lock (SyncRoot)
        {
            Start();
            var actual = _options!.Merge(options);
            var player = new Player(new PlaybackSession(actual));
            ActivePlayers.Add(player.InstanceId, player);
            return player;
        }
    }

    /// <summary>
    /// 停止所有播放器并保留引擎配置。
    /// </summary>
    public static async Task StopAsync(CancellationToken cancellationToken = default)
    {
        Player[] players;
        lock (SyncRoot) players = ActivePlayers.Values.ToArray();
        await Task.WhenAll(players.Select(player => player.StopAsync(cancellationToken).AsTask())).ConfigureAwait(false);
    }
    /// <summary>
    /// 同步等待所有播放器停止。
    /// </summary>
    public static void Stop() => StopAsync().GetAwaiter().GetResult();
    /// <summary>
    /// 释放全部播放器后再关闭原生环境；重复调用共享任务。
    /// </summary>
    public static Task ShutdownAsync()
    {
        lock (SyncRoot)
        {
            if (_shutdownTask is not null) return _shutdownTask;
            if (_options is null && ActivePlayers.Count == 0) return Task.CompletedTask;
            var players = ActivePlayers.Values.ToArray();
            var generation = _logGeneration;
            return _shutdownTask = Task.Run(() => ShutdownCoreAsync(players, generation));
        }
    }
    /// <summary>
    /// 同步等待引擎关闭。
    /// </summary>
    public static void Shutdown() => ShutdownAsync().GetAwaiter().GetResult();
    /// <summary>
    /// 等待播放器归还资源后清除共享状态。
    /// </summary>
    private static async Task ShutdownCoreAsync(Player[] players, long? generation)
    {
        try
        {
            await Task.WhenAll(players.Select(player => player.DisposeAsync().AsTask())).ConfigureAwait(false);
            FFmpegNativeRuntime.Shutdown();
            FluxMediaLog.Flush();
            if (generation.HasValue) FluxMediaLog.ShutdownIfCurrent(generation.Value);
        }
        finally
        {
            lock (SyncRoot)
            {
                ActivePlayers.Clear();
                _options = null;
                _logGeneration = null;
                _shutdownTask = null;
            }
        }
    }
    /// <summary>
    /// 播放器完成释放后解除登记。
    /// </summary>
    internal static void Unregister(Player player)
    {
        lock (SyncRoot) ActivePlayers.Remove(player.InstanceId);
    }
}

/// <summary>
/// 引擎的原生库、日志及默认超时配置。
/// </summary>
public sealed record FluxEngineOptions
{
    /// <summary>
    /// FFmpeg 共享库目录。
    /// </summary>
    public string? FFmpegPath { get; init; }
    /// <summary>
    /// 附加原生库搜索目录。
    /// </summary>
    public IReadOnlyList<string> FFmpegSearchPaths { get; init; } = Array.Empty<string>();
    /// <summary>
    /// 缺少原生库时是否立即失败。
    /// </summary>
    public bool ThrowOnMissingFFmpeg { get; init; }
    /// <summary>
    /// 是否由引擎创建文件日志。
    /// </summary>
    public bool EnableLogging { get; init; }
    /// <summary>
    /// 引擎日志目录。
    /// </summary>
    public string LogDirectory { get; init; } = "Logs";
    /// <summary>
    /// 引擎日志最低级别。
    /// </summary>
    public LogLevel LogLevel { get; init; } = LogLevel.Information;
    /// <summary>
    /// 默认媒体打开超时。
    /// </summary>
    public TimeSpan DefaultOpenTimeout { get; init; } = TimeSpan.FromSeconds(15);
    /// <summary>
    /// 转换为唯一的原生库配置模型。
    /// </summary>
    internal NativeLibraryOptions NativeOptions => new()
    {
        FFmpegRootDirectory = FFmpegPath,
        AdditionalSearchDirectories = FFmpegSearchPaths,
        ThrowOnMissingLibrary = ThrowOnMissingFFmpeg
    };
    /// <summary>
    /// 合并引擎默认值和播放器显式参数，原生库配置仅在此合并一次。
    /// </summary>
    internal PlaybackOptions Merge(PlaybackOptions? options)
    {
        var explicitNative = options?.NativeLibraries ?? new NativeLibraryOptions();
        var actual = options ?? new PlaybackOptions { OpenTimeout = this.DefaultOpenTimeout };
        actual = actual with
        {
            NativeLibraries = new NativeLibraryOptions
            {
                FFmpegRootDirectory = string.IsNullOrWhiteSpace(explicitNative.FFmpegRootDirectory) ? this.FFmpegPath : explicitNative.FFmpegRootDirectory,
                AdditionalSearchDirectories = this.FFmpegSearchPaths.Concat(explicitNative.AdditionalSearchDirectories)
                    .Where(static path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                ThrowOnMissingLibrary = this.ThrowOnMissingFFmpeg || explicitNative.ThrowOnMissingLibrary
            }
        };

        return actual;
    }
}
