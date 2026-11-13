using FFmpeg.AutoGen;
using FluxMedia.Core.Configuration;
using FluxMedia.Core.Logging;
using FluxMedia.Playback;
using System.Runtime.InteropServices;

namespace FluxMedia.FFmpeg.Native;

/// <summary>
/// 管理显式初始化的进程共享 FFmpeg 环境。
/// </summary>
internal static unsafe class FFmpegNativeRuntime
{
    /// <summary>
    /// 初始化与关闭共用的锁。
    /// </summary>
    private static readonly object SyncRoot = new();
    /// <summary>
    /// 所有初始化步骤成功后才置为就绪。
    /// </summary>
    private static volatile bool _isInitialized;
    /// <summary>
    /// 已加载原生库所属目录，进程内不再切换。
    /// </summary>
    private static string? _loadedRootPath;

    /// <summary>
    /// 加载并校验原生库，任何失败直接抛出异常。
    /// </summary>
    public static void Initialize(string? directory = null)
    {
        lock (SyncRoot)
        {
            if (_isInitialized) return;
            var loader = new NativeLibraryLoader(directory);
            var networkInitialized = false;
            try
            {
                var rootPath = loader.ResolveDirectory();
                var comparison = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
                if (_loadedRootPath is not null && !string.Equals(_loadedRootPath, rootPath, comparison))
                {
                    throw new InvalidOperationException("当前进程已加载另一目录的 FFmpeg，切换原生库目录后请重新启动进程。");
                }
                ffmpeg.RootPath = rootPath;
                foreach (var descriptor in FFmpegLibraries.RequiredLibraries)
                {
                    loader.Load(descriptor, rootPath);
                    _loadedRootPath ??= rootPath;
                }
                FFmpegLibraries.ValidateVersions();
                var version = ffmpeg.av_version_info();
                var result = ffmpeg.avformat_network_init();
                if (result < 0) throw new InvalidOperationException($"FFmpeg 网络初始化失败：{result}。");
                networkInitialized = true;
                FluxMediaLog.Write(LogLevel.Information, nameof(FFmpegNativeRuntime), Guid.Empty,
                    $"Event=FFmpegInitialized BindingVersion={FFmpegLibraries.BindingVersion} Version={version} RootPath={rootPath}");
                _isInitialized = true;
            }
            catch (Exception exception)
            {
                _isInitialized = false;
                if (networkInitialized) ffmpeg.avformat_network_deinit();
                var message = $"FFmpeg 初始化失败：{exception.Message}";
                FluxMediaLog.Write(LogLevel.Error, nameof(FFmpegNativeRuntime), Guid.Empty,
                    $"Event=FFmpegInitializeFailed Error={exception.Message}", exception);
                throw new InvalidOperationException(message, exception);
            }
        }
    }

    /// <summary>
    /// 仅检查就绪状态，不执行初始化或重试。
    /// </summary>
    public static void EnsureInitialized()
    {
        if (!_isInitialized)
        {
            throw new InvalidOperationException("FFmpeg 尚未初始化，请先成功调用 FluxEngine.Start。");
        }
    }

    /// <summary>
    /// 关闭网络环境，原生库和函数绑定仍保留至进程退出。
    /// </summary>
    public static void Shutdown()
    {
        lock (SyncRoot)
        {
            if (!_isInitialized) return;
            try
            {
                ffmpeg.avformat_network_deinit();
            }
            finally
            {
                _isInitialized = false;
            }
        }
    }
}
