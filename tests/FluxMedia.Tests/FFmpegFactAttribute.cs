using FluxMedia.Playback;
using System.Runtime.InteropServices;

namespace FluxMedia.Tests;

/// <summary>
/// 缺少与当前绑定匹配的本地依赖时跳过测试。
/// </summary>
public sealed class FFmpegFactAttribute : FactAttribute
{
    /// <summary>
    /// 优先使用指定的原生库目录。
    /// </summary>
    internal static string NativeRoot
    {
        get
        {
            var configured = Environment.GetEnvironmentVariable("FLUXMEDIA_FFMPEG_ROOT");
            return !string.IsNullOrWhiteSpace(configured) ? Path.GetFullPath(configured) : Path.GetFullPath(Path.Combine(
                AppContext.BaseDirectory, "..", "..", "..", "..", "..", "FFmpeg", RuntimeInformation.RuntimeIdentifier, "native"));
        }
    }

    /// <summary>
    /// 检查依赖文件，不加载原生库。
    /// </summary>
    public FFmpegFactAttribute()
    {
        if (!FFmpegLibraries.IsAvailable(NativeRoot))
        {
            Skip = $"需要与当前 FFmpeg.AutoGen 绑定匹配的共享库：{string.Join(", ", FFmpegLibraries.RequiredFileNames)}。";
        }
    }
}
