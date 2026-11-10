using System.Runtime.InteropServices;
using FluxMedia.Core.Configuration;
using FluxMedia.Core.Logging;

namespace FluxMedia.FFmpeg.Native;

/// <summary>
/// 统一的 Native Library 加载器，负责搜索路径构建、加载和日志记录。
/// </summary>
internal sealed class NativeLibraryLoader
{
    /// <summary>
    /// 配置。
    /// </summary>
    private readonly NativeLibraryOptions _options;

    /// <summary>
    /// 实例标识。
    /// </summary>
    private readonly Guid _instanceId = Guid.NewGuid();

    /// <summary>
    /// 初始化原生库加载器所需的状态和资源。
    /// </summary>
    public NativeLibraryLoader(NativeLibraryOptions? options = null)
    {
        _options = options ?? new NativeLibraryOptions();
    }

    /// <summary>
    /// 根据配置、环境变量、运行目录和 RID 约定构建原生库搜索路径。
    /// </summary>
    public IReadOnlyList<string> BuildSearchDirectories()
    {
        var directories = new List<string>();

        if (!string.IsNullOrWhiteSpace(_options.FFmpegRootDirectory))
        {
            directories.Add(_options.FFmpegRootDirectory);
        }

        var environmentRoot = Environment.GetEnvironmentVariable("FLUXMEDIA_FFMPEG_ROOT");
        if (!string.IsNullOrWhiteSpace(environmentRoot))
        {
            directories.Add(environmentRoot);
        }

        directories.AddRange(_options.AdditionalSearchDirectories.Where(static item => !string.IsNullOrWhiteSpace(item)));
        directories.Add(AppContext.BaseDirectory);
        var runtimeIdentifier = GetRuntimeIdentifier();
        directories.Add(Path.Combine(AppContext.BaseDirectory, "runtimes", runtimeIdentifier, "native"));
        directories.Add(Path.Combine(AppContext.BaseDirectory, "ffmpeg", runtimeIdentifier));
        directories.Add(Path.Combine(AppContext.BaseDirectory, "FFmpeg", RuntimeInformation.ProcessArchitecture == Architecture.X64 ? "x64" : RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant()));

        return directories
            .Where(static directory => !string.IsNullOrWhiteSpace(directory))
            .Distinct(RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>
    /// 在指定目录或候选目录中加载原生库。
    /// </summary>
    public NativeLibraryLoadResult TryLoad(NativeLibraryDescriptor descriptor, string? directory = null)
    {
        Exception? lastError = null;
        string? lastCandidate = null;
        IReadOnlyList<string> directories = directory is null ? BuildSearchDirectories() : new[] { directory };
        foreach (var searchDirectory in directories)
        {
            foreach (var candidate in EnumerateCandidates(searchDirectory, descriptor.FileNames))
            {
                lastCandidate = candidate;
                FluxMediaLog.Write(LogLevel.Debug, nameof(NativeLibraryLoader), _instanceId, $"Event=NativeLibrarySearch Path={candidate}");

                try
                {
                    NativeLibraryCompat.Load(candidate);
                    FluxMediaLog.Write(LogLevel.Information, nameof(NativeLibraryLoader), _instanceId, $"Event=NativeLibraryLoaded Path={candidate}");
                    return new NativeLibraryLoadResult(descriptor, candidate, true);
                }
                catch (Exception ex)
                {
                    lastError = ex;
                    FluxMediaLog.Write(LogLevel.Error, nameof(NativeLibraryLoader), _instanceId, $"Event=NativeLibraryLoadFailed Path={candidate}", ex);
                }
            }
        }

        if (lastError is not null)
        {
            if (_options.ThrowOnMissingLibrary)
                throw new InvalidOperationException($"所有候选路径均无法加载 {descriptor.LogicalName}。", lastError);
            return new NativeLibraryLoadResult(descriptor, lastCandidate, false, lastError);
        }

        var searchText = string.Join(Path.PathSeparator.ToString(), directories);
        if (_options.ThrowOnMissingLibrary)
            throw new FileNotFoundException($"未找到原生库 {descriptor.LogicalName}。已搜索路径：{searchText}");
        return new NativeLibraryLoadResult(
            descriptor, null, false, new FileNotFoundException($"未找到原生库 {descriptor.LogicalName}。已搜索路径：{searchText}"));
    }

    /// <summary>
    /// 枚举当前平台可能使用的原生库路径。
    /// </summary>
    private static IEnumerable<string> EnumerateCandidates(string directory, IReadOnlyList<string> fileNames)
    {
        if (!Directory.Exists(directory))
        {
            yield break;
        }

        foreach (var fileName in fileNames)
        {
            if (fileName.IndexOf('*') >= 0)
            {
                foreach (var file in Directory.EnumerateFiles(directory, fileName))
                {
                    yield return file;
                }
            }
            else
            {
                var path = Path.Combine(directory, fileName);
                if (File.Exists(path))
                {
                    yield return path;
                }
            }
        }
    }

    /// <summary>
    /// 获取当前平台和架构对应的运行时标识。
    /// </summary>
    private static string GetRuntimeIdentifier()
    {
        var os = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? "win"
            : RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
                ? "osx"
                : "linux";
        var architecture = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.X86 => "x86",
            Architecture.Arm64 => "arm64",
            Architecture.Arm => "arm",
            _ => RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant()
        };
        return $"{os}-{architecture}";
    }
}
