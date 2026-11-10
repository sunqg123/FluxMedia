namespace FluxMedia.Core.Models;

/// <summary>
/// 编解码器信息。
/// </summary>
/// <param name="Name">编解码器名称。</param>
/// <param name="Profile">编解码器使用的配置档次。</param>
/// <param name="BitrateKbps">码率，单位为千比特每秒。</param>
public sealed record CodecInfo(string Name, string? Profile = null, int? BitrateKbps = null);
