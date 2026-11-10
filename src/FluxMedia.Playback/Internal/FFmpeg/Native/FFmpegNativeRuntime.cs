using FFmpeg.AutoGen;
using FluxMedia.Core.Configuration;
using FluxMedia.Core.Logging;
using FluxMedia.Playback;

namespace FluxMedia.FFmpeg.Native;

/// <summary>
/// 初始化 FFmpeg 库、网络组件并检查版本。
/// </summary>
internal static unsafe class FFmpegNativeRuntime
{
    /// <summary>
    /// 同步锁。
    /// </summary>
    private static readonly object SyncRoot = new();

    /// <summary>
    /// 最近结果。
    /// </summary>
    private static FFmpegInitializeResult? _lastResult;

    /// <summary>
    /// 是否已初始化。
    /// </summary>
    public static bool IsInitialized => _lastResult?.IsInitialized == true;

    /// <summary>
    /// 最近结果。
    /// </summary>
    public static FFmpegInitializeResult? LastResult => _lastResult;

    /// <summary>
    /// 加载并检查 FFmpeg 原生库，初始化网络组件。
    /// </summary>
    public static FFmpegInitializeResult Initialize(NativeLibraryOptions? options = null)
    {
        lock (SyncRoot)
        {
            if (_lastResult?.IsInitialized == true)
            {
                return _lastResult;
            }

            var actualOptions = (options ?? new NativeLibraryOptions());
            var loader = new NativeLibraryLoader(actualOptions);
            var searchDirectories = loader.BuildSearchDirectories();

            try
            {
                var rootPath = ResolveFFmpegRoot(searchDirectories);
                if (rootPath is null)
                {
                    throw new DirectoryNotFoundException(
                        $"未找到与 FFmpeg.AutoGen {FFmpegLibraries.BindingVersion} 匹配的完整目录。需要：{string.Join(", ", FFmpegLibraries.RequiredFileNames)}。已搜索路径：{string.Join(Path.PathSeparator.ToString(), searchDirectories)}");
                }

                ffmpeg.RootPath = rootPath;
                FluxMediaLog.Write(LogLevel.Information, nameof(FFmpegNativeRuntime), Guid.Empty, $"Event=FFmpegRootResolved BindingVersion={FFmpegLibraries.BindingVersion} RootPath={rootPath}");

                LoadRequiredLibraries(loader, rootPath);
                FFmpegLibraries.ValidateVersions();
                var networkResult = ffmpeg.avformat_network_init();
                if (networkResult < 0)
                    throw new InvalidOperationException($"FFmpeg 网络初始化失败：{networkResult}。");
                var version = ffmpeg.av_version_info();

                _lastResult = new FFmpegInitializeResult(true, rootPath, version, searchDirectories);
                FluxMediaLog.Write(LogLevel.Information, nameof(FFmpegNativeRuntime), Guid.Empty, $"Event=FFmpegInitialized BindingVersion={FFmpegLibraries.BindingVersion} Version={version} RootPath={rootPath}");
                return _lastResult;
            }
            catch (Exception ex)
            {
                var message = $"FFmpeg 初始化失败：{ex.Message}";
                FluxMediaLog.Write(LogLevel.Error, nameof(FFmpegNativeRuntime), Guid.Empty, $"Event=FFmpegInitializeFailed Error={ex.Message}", ex);
                _lastResult = new FFmpegInitializeResult(false, null, null, searchDirectories, message);

                if (actualOptions.ThrowOnMissingLibrary)
                {
                    throw;
                }

                return _lastResult;
            }
        }
    }

    /// <summary>
    /// 确保 FFmpeg 原生环境已初始化。
    /// </summary>
    public static void EnsureInitialized(NativeLibraryOptions? options = null)
    {
        var result = Initialize(options);
        if (!result.IsInitialized)
        {
            throw new InvalidOperationException(result.ErrorMessage ?? "FFmpeg 初始化失败。");
        }
    }

    /// <summary>
    /// 关闭共享运行时并释放相关资源。
    /// </summary>
    public static void Shutdown()
    {
        lock (SyncRoot)
        {
            if (_lastResult is null)
            {
                FluxMediaLog.Write(LogLevel.Debug, nameof(FFmpegNativeRuntime), Guid.Empty, "Event=FFmpegShutdownSkipped Reason=NotInitialized");
                return;
            }

            try
            {
                if (_lastResult.IsInitialized)
                {
                    ffmpeg.avformat_network_deinit();
                    FluxMediaLog.Write(LogLevel.Information, nameof(FFmpegNativeRuntime), Guid.Empty, $"Event=FFmpegShutdown RootPath={_lastResult.RootPath ?? "-"}");
                }
                else
                {
                    FluxMediaLog.Write(LogLevel.Debug, nameof(FFmpegNativeRuntime), Guid.Empty, $"Event=FFmpegShutdownSkipped Reason=NotInitialized Error={_lastResult.ErrorMessage ?? "-"}");
                }
            }
            finally
            {
                _lastResult = null;
            }
        }
    }

    /// <summary>
    /// 查找与当前绑定匹配的完整原生库目录。
    /// </summary>
    private static string? ResolveFFmpegRoot(IReadOnlyList<string> searchDirectories) =>
        searchDirectories.FirstOrDefault(FFmpegLibraries.IsAvailable);

    /// <summary>
    /// 从同一目录按依赖顺序加载原生库，避免混用构建。
    /// </summary>
    private static void LoadRequiredLibraries(NativeLibraryLoader loader, string rootPath)
    {
        foreach (var descriptor in FFmpegLibraries.RequiredLibraries)
        {
            var result = loader.TryLoad(descriptor, rootPath);
            if (!result.IsLoaded) throw new FileNotFoundException(result.ErrorMessage);
        }
    }
}
