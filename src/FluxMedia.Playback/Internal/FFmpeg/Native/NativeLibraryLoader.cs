using FluxMedia.Core.Configuration;
using FluxMedia.Core.Logging;
using FluxMedia.Playback;

namespace FluxMedia.FFmpeg.Native;

/// <summary>
/// 选择完整的配置目录，并从该目录加载原生库。
/// </summary>
internal sealed class NativeLibraryLoader
{
    /// <summary>
    /// 显式指定的目录配置。
    /// </summary>
    private readonly string? _explicitDirectory;
    /// <summary>
    /// 本次加载的日志标识。
    /// </summary>
    private readonly Guid _instanceId = Guid.NewGuid();

    /// <summary>
    /// 保存显式目录配置。
    /// </summary>
    public NativeLibraryLoader(string? explicitDirectory = null)
    {
        _explicitDirectory = explicitDirectory;
    }

    /// <summary>
    /// 显式目录优先，环境变量仅在目录不可用时兜底。
    /// </summary>
    public string ResolveDirectory()
    {
        var explicitRoot = ResolveConfiguredDirectory(_explicitDirectory, "Explicit");
        if (explicitRoot is not null) return explicitRoot;
        var environment = Environment.GetEnvironmentVariable("FLUXMEDIA_FFMPEG_ROOT");
        var environmentRoot = ResolveConfiguredDirectory(environment, "Environment");
        if (environmentRoot is not null) return environmentRoot;
        throw new DirectoryNotFoundException(
            $"未找到与 FFmpeg.AutoGen {FFmpegLibraries.BindingVersion} 匹配的完整库目录。" +
            $"显式目录：{_explicitDirectory ?? "未配置"}；FLUXMEDIA_FFMPEG_ROOT：{environment ?? "未配置"}。" +
            $"需要：{string.Join(", ", FFmpegLibraries.RequiredFileNames)}。");
    }

    /// <summary>
    /// 检查完整库集合，不加载原生代码。
    /// </summary>
    private string? ResolveConfiguredDirectory(string? directory, string source)
    {
        if (string.IsNullOrWhiteSpace(directory)) return null;
        if (!FFmpegLibraries.IsAvailable(directory))
        {
            FluxMediaLog.Write(LogLevel.Warning, nameof(NativeLibraryLoader), _instanceId,
                $"Event=FFmpegDirectoryRejected Source={source} Directory={directory} Reason=MissingOrIncomplete");
            return null;
        }
        return Path.GetFullPath(directory);
    }

    /// <summary>
    /// 仅加载选定目录中的准确文件，不扫描或切换目录。
    /// </summary>
    public void Load(NativeLibraryDescriptor descriptor, string directory)
    {
        var path = Path.Combine(directory, descriptor.FileName);
        try
        {
            NativeLibraryCompat.Load(path);
            FluxMediaLog.Write(LogLevel.Information, nameof(NativeLibraryLoader), _instanceId,
                $"Event=NativeLibraryLoaded Path={path}");
        }
        catch (Exception exception)
        {
            FluxMediaLog.Write(LogLevel.Error, nameof(NativeLibraryLoader), _instanceId,
                $"Event=NativeLibraryLoadFailed Path={path}", exception);
            throw new InvalidOperationException($"无法加载原生库 {descriptor.LogicalName}：{path}。请检查平台、进程架构和依赖。", exception);
        }
    }
}
