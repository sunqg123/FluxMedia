namespace FluxMedia.Core.Configuration;

/// <summary>
/// 原生库的路径和加载配置。
/// </summary>
public sealed record NativeLibraryOptions
{
    /// <summary>
    /// FFmpeg 共享库目录。
    /// </summary>
    public string? FFmpegRootDirectory { get; init; }

    /// <summary>
    /// 附加搜索目录。
    /// </summary>
    public IReadOnlyList<string> AdditionalSearchDirectories { get; init; } = Array.Empty<string>();

    /// <summary>
    /// 缺少原生库时是否抛出异常。
    /// </summary>
    public bool ThrowOnMissingLibrary { get; init; }
}

