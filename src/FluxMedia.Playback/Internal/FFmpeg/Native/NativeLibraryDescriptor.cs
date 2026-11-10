namespace FluxMedia.FFmpeg.Native;

/// <summary>
/// 原生库描述，用逻辑名称映射不同平台上的文件名。
/// </summary>
/// <param name="LogicalName">原生库的平台无关名称。</param>
/// <param name="FileNames">不同平台可能使用的原生库文件名。</param>
internal sealed record NativeLibraryDescriptor(string LogicalName, IReadOnlyList<string> FileNames);

/// <summary>
/// 原生库加载结果。
/// </summary>
/// <param name="Descriptor">需要加载的原生库描述。</param>
/// <param name="LoadedPath">实际成功加载的原生库路径。</param>
/// <param name="IsLoaded">是否成功加载原生库。</param>
/// <param name="Exception">产生当前错误的原始异常。</param>
internal sealed record NativeLibraryLoadResult(
    NativeLibraryDescriptor Descriptor, string? LoadedPath, bool IsLoaded, Exception? Exception = null)
{
    /// <summary>
    /// 错误消息。
    /// </summary>
    public string ErrorMessage => IsLoaded
        ? string.Empty
        : Exception?.Message ?? $"未找到原生库：{Descriptor.LogicalName}";
}

/// <summary>
/// FFmpeg 初始化结果。
/// </summary>
/// <param name="IsInitialized">是否成功完成初始化。</param>
/// <param name="RootPath">实际采用的 FFmpeg 根目录。</param>
/// <param name="Version">已加载 FFmpeg 原生库的版本。</param>
/// <param name="SearchDirectories">初始化时检查过的原生库目录。</param>
/// <param name="ErrorMessage">失败原因的文本说明。</param>
internal sealed record FFmpegInitializeResult(
    bool IsInitialized, string? RootPath, string? Version, IReadOnlyList<string> SearchDirectories, string? ErrorMessage = null);
