namespace FluxMedia.FFmpeg.Native;

/// <summary>
/// 当前平台和绑定对应的原生库文件。
/// </summary>
/// <param name="LogicalName">原生库逻辑名称。</param>
/// <param name="FileName">当前平台的准确文件名。</param>
internal sealed record NativeLibraryDescriptor(string LogicalName, string FileName);
