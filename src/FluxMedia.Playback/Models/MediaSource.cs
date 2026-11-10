
namespace FluxMedia.Core.Models;

/// <summary>
/// 媒体输入源类型。
/// </summary>
public enum MediaSourceKind
{
    /// <summary>
    /// 文件。
    /// </summary>
    File,
    /// <summary>
    /// 网络流。
    /// </summary>
    NetworkStream
}


/// <summary>
/// 媒体源的地址和类型。
/// </summary>
/// <param name="Kind">当前对象的媒体源或表面类型。</param>
/// <param name="Uri">媒体文件路径或网络地址。</param>
public sealed record MediaSource(MediaSourceKind Kind, string Uri)
{
    /// <summary>
    /// 创建本地文件媒体源。
    /// </summary>
    public static MediaSource FromFile(string path) => new(MediaSourceKind.File, path);

    /// <summary>
    /// 创建网络媒体源。
    /// </summary>
    public static MediaSource FromNetwork(string uri) => new(MediaSourceKind.NetworkStream, uri);
}
