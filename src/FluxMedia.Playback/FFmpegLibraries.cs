using FFmpeg.AutoGen;
using FluxMedia.FFmpeg.Native;
using System.Runtime.InteropServices;

namespace FluxMedia.Playback;

/// <summary>
/// 根据当前托管绑定统一管理 FFmpeg 库名和 ABI。
/// </summary>
public static class FFmpegLibraries
{
    /// <summary>
    /// 当前实际引用的托管绑定程序集版本。
    /// </summary>
    public static string BindingVersion { get; } = typeof(ffmpeg).Assembly.GetName().Version?.ToString() ?? "未知";

    /// <summary>
    /// 绑定声明的 ABI 主版本，顺序同时作为加载顺序。
    /// </summary>
    private static readonly (string Name, int Major)[] Libraries =
    {
        ("avutil", ffmpeg.LIBAVUTIL_VERSION_MAJOR),
        ("swresample", ffmpeg.LIBSWRESAMPLE_VERSION_MAJOR),
        ("swscale", ffmpeg.LIBSWSCALE_VERSION_MAJOR),
        ("avcodec", ffmpeg.LIBAVCODEC_VERSION_MAJOR),
        ("avformat", ffmpeg.LIBAVFORMAT_VERSION_MAJOR),
        ("avfilter", ffmpeg.LIBAVFILTER_VERSION_MAJOR)
    };

    /// <summary>
    /// 当前绑定在本平台需要的原生库。
    /// </summary>
    internal static IReadOnlyList<NativeLibraryDescriptor> RequiredLibraries { get; } = Array.AsReadOnly(Libraries.Select(library => new NativeLibraryDescriptor(library.Name, GetFileName(library.Name, library.Major))).ToArray());

    /// <summary>
    /// 当前平台所需的完整库名，不加载原生库。
    /// </summary>
    public static IReadOnlyList<string> RequiredFileNames { get; } = Array.AsReadOnly(RequiredLibraries.Select(library => library.FileName).ToArray());

    /// <summary>
    /// 只检查完整库文件是否存在，不加载或执行原生代码。
    /// </summary>
    public static bool IsAvailable(string? directory) => !string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory) && RequiredFileNames.All(fileName => File.Exists(Path.Combine(directory!, fileName)));

    /// <summary>
    /// 按平台规则生成与绑定匹配的库名。
    /// </summary>
    private static string GetFileName(string library, int major)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return $"{library}-{major}.dll";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return $"lib{library}.so.{major}";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return $"lib{library}.{major}.dylib";
        throw new PlatformNotSupportedException("FFmpeg 库加载仅支持 Windows、Linux 和 macOS。");
    }

    /// <summary>
    /// 在访问原生结构之前核对实际 ABI。
    /// </summary>
    internal static void ValidateVersions()
    {
        RequireVersion("avutil", ffmpeg.avutil_version());
        RequireVersion("swresample", ffmpeg.swresample_version());
        RequireVersion("swscale", ffmpeg.swscale_version());
        RequireVersion("avcodec", ffmpeg.avcodec_version());
        RequireVersion("avformat", ffmpeg.avformat_version());
        RequireVersion("avfilter", ffmpeg.avfilter_version());
    }

    /// <summary>
    /// 拒绝与当前绑定不一致的原生库主版本。
    /// </summary>
    private static void RequireVersion(string library, uint version)
    {
        var expected = Libraries.First(item => item.Name == library).Major;
        var actual = version >> 16;
        if (actual != expected)
            throw new InvalidOperationException($"{library} 实际 ABI 主版本为 {actual}，当前 FFmpeg.AutoGen 绑定要求 {expected}。");
    }
}
