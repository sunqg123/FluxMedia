using FluxMedia.Core.Models;

namespace FluxMedia.Playback.Internal;

/// <summary>
/// 统一解析媒体地址并校验打开条件。
/// </summary>
internal static class MediaSourcePolicy
{
    /// <summary>
    /// 当前允许的网络协议。
    /// </summary>
    private static readonly HashSet<string> NetworkSchemes = new(StringComparer.OrdinalIgnoreCase)
    {
        "http", "https", "rtsp", "rtmp"
    };

    /// <summary>
    /// 按地址语法区分文件与网络输入。
    /// </summary>
    internal static MediaSource Parse(string? value)
    {
        var address = value ?? string.Empty;
        if (IsFilePath(address) || !TryGetScheme(address, out var scheme) || scheme == "file")
        {
            return MediaSource.FromFile(address);
        }

        return MediaSource.FromNetwork(address);
    }

    /// <summary>
    /// 校验媒体源，并返回实际打开的地址。
    /// </summary>
    internal static bool TryValidate(MediaSource? source, out MediaSource actualSource, out string error)
    {
        actualSource = null!;
        error = string.Empty;
        if (source is null)
        {
            error = "媒体源不能为空。";
            return false;
        }
        if (string.IsNullOrWhiteSpace(source.Uri))
        {
            error = "媒体地址不能为空。";
            return false;
        }

        switch (source.Kind)
        {
            case MediaSourceKind.File:
                var path = source.Uri;
                if (!IsFilePath(path) && TryGetScheme(path, out var fileScheme) && fileScheme == "file")
                {
                    if (!Uri.TryCreate(path, UriKind.Absolute, out var fileUri) || !fileUri.IsFile)
                    {
                        error = "本地文件地址格式不正确。";
                        return false;
                    }
                    path = fileUri.LocalPath;
                }
                if (!File.Exists(path))
                {
                    error = $"媒体文件不存在：{path}";
                    return false;
                }
                actualSource = source with
                {
                    Uri = path
                };
                return true;

            case MediaSourceKind.NetworkStream:
                var address = source.Uri.Trim();
                if (!Uri.TryCreate(address, UriKind.Absolute, out var uri))
                {
                    error = "网络媒体地址格式不正确。";
                    return false;
                }
                if (!NetworkSchemes.Contains(uri.Scheme))
                {
                    error = $"不支持网络协议：{uri.Scheme}。";
                    return false;
                }
                if (string.IsNullOrWhiteSpace(uri.Host))
                {
                    error = "网络媒体地址缺少主机。";
                    return false;
                }
                actualSource = source with
                {
                    Uri = address
                };
                return true;

            default:
                error = "媒体源类型不受支持。";
                return false;
        }
    }

    /// <summary>
    /// 返回统一的文件或网络协议名称。
    /// </summary>
    internal static string? GetProtocol(MediaSource? source)
    {
        if (source is null)
            return null;
        if (source.Kind == MediaSourceKind.File)
            return "file";
        return TryGetScheme(source.Uri, out var scheme) ? scheme : null;
    }

    /// <summary>
    /// 优先识别绝对路径、共享路径和盘符路径。
    /// </summary>
    private static bool IsFilePath(string value)
    {
        if (value.Length == 0)
            return false;
        if (value[0] is '/' or '\\')
            return true;
        return value.Length >= 2 && char.IsLetter(value[0]) && value[1] == ':';
    }

    /// <summary>
    /// 从地址前缀提取协议，不判断支持范围。
    /// </summary>
    private static bool TryGetScheme(string? value, out string scheme)
    {
        scheme = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
            return false;
        var address = value!.TrimStart();
        var separator = address.IndexOf(':');
        if (separator <= 0)
            return false;
        var candidate = address.Substring(0, separator);
        if (!Uri.CheckSchemeName(candidate))
            return false;
        scheme = candidate.ToLowerInvariant();
        return true;
    }
}
