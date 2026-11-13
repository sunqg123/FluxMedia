using LogConverge.Sharp;
using FluxMedia.FFmpeg.Native;
using FluxMedia.Playback.Internal;

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
    /// 并发关闭共享的完成任务。
    /// </summary>
    private static Task? _shutdownTask;

    /// <summary>
    /// 是否已启动且未进入关闭流程。
    /// </summary>
    public static bool IsStarted
    {
        get
        {
            lock (SyncRoot)
            {
                return _options is not null && _shutdownTask is null;
            }
        }
    }
    /// <summary>
    /// 当前由引擎管理的播放器快照。
    /// </summary>
    public static IReadOnlyCollection<Player> Players
    {
        get
        {
            lock (SyncRoot)
            {
                return Array.AsReadOnly(ActivePlayers.Values.ToArray());
            }
        }
    }

    /// <summary>
    /// 显式初始化共享环境，失败直接抛出异常。
    /// </summary>
    public static void Start(FluxEngineOptions? options = null)
    {
        lock (SyncRoot)
        {
            if (_shutdownTask is not null)
                throw new InvalidOperationException("引擎正在关闭，请等待 ShutdownAsync 完成。");
            if (_options is not null)
            {
                if (options is not null && options != _options)
                {
                    throw new InvalidOperationException("引擎已经启动，不能在运行期间更换初始化配置。");
                }
                return;
            }
            var actual = options ?? new FluxEngineOptions();
            PlaybackRatePolicy.ValidateOptions(actual);
            var logResult = Log.Initialize(Path.Combine(AppContext.BaseDirectory, "Logs"), actual.EnableLogging);
            if (logResult is not LogResult.Ok and not LogResult.AlreadyInitialized)
                throw new InvalidOperationException($"日志初始化失败：{logResult}。");
            try
            {
                FFmpegNativeRuntime.Initialize(actual.FFmpegPath);
                _options = actual;
                Log.Info(nameof(FluxEngine), "Event=EngineStarted");
            }
            catch (Exception exception)
            {
                Log.Error(nameof(FluxEngine), "Event=EngineStartFailed", exception);
                throw;
            }
        }
    }

    /// <summary>
    /// 在已启动的引擎中创建播放器，未初始化时抛出异常。
    /// </summary>
    public static Player CreatePlayer(PlaybackOptions? options = null)
    {
        lock (SyncRoot)
        {
            if (_shutdownTask is not null)
                throw new InvalidOperationException("引擎正在关闭，不能创建播放器。");
            if (_options is null)
                throw new InvalidOperationException("引擎尚未初始化，请先调用 FluxEngine.Start。");
            var actual = options ?? new PlaybackOptions();
            var player = new Player(new PlaybackSession(_options, actual));
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
        lock (SyncRoot)
            players = ActivePlayers.Values.ToArray();
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
            if (_shutdownTask is not null)
                return _shutdownTask;
            if (_options is null && ActivePlayers.Count == 0)
                return Task.CompletedTask;
            var players = ActivePlayers.Values.ToArray();
            return _shutdownTask = Task.Run(() => ShutdownCoreAsync(players));
        }
    }
    /// <summary>
    /// 同步等待引擎关闭。
    /// </summary>
    public static void Shutdown() => ShutdownAsync().GetAwaiter().GetResult();
    /// <summary>
    /// 等待播放器归还资源后清除共享状态。
    /// </summary>
    private static async Task ShutdownCoreAsync(Player[] players)
    {
        var errors = new List<Exception>();
        try
        {
            try
            {
                await Task.WhenAll(players.Select(player => player.DisposeAsync().AsTask())).ConfigureAwait(false);
            }
            catch (Exception exception) { errors.Add(exception); }
            Release(FFmpegNativeRuntime.Shutdown);
        }
        finally
        {
            lock (SyncRoot)
            {
                ActivePlayers.Clear();
                _options = null;
                _shutdownTask = null;
            }
        }
        if (errors.Count > 0)
        {
            var failure = new AggregateException("引擎关闭期间发生资源释放错误。", errors);
            Log.Error(nameof(FluxEngine), "Event=EngineShutdownFailed", failure);
            throw failure;
        }
        Log.Info(nameof(FluxEngine), "Event=EngineStopped");

        // 播放器释放失败后仍关闭共享原生环境。
        void Release(Action release)
        {
            try
            {
                release();
            }
            catch (Exception exception) { errors.Add(exception); }
        }
    }
    /// <summary>
    /// 播放器完成释放后解除登记。
    /// </summary>
    internal static void Unregister(Player player)
    {
        lock (SyncRoot)
            ActivePlayers.Remove(player.InstanceId);
    }
}
