using Avalonia;

namespace FluxMedia.Avalonia;

/// <summary>
/// 视频控件的显示状态和帧统计。
/// </summary>
public sealed record FluxPlayerViewDiagnosticInfo
{
    /// <summary>
    /// 呈现管线。
    /// </summary>
    public string PresentationPipeline { get; init; } = string.Empty;

    /// <summary>
    /// 后端选择。
    /// </summary>
    public string BackendSelection { get; init; } = string.Empty;

    /// <summary>
    /// 是否使用 Avalonia 合成器。
    /// </summary>
    public bool UsesAvaloniaCompositor
    {
        get; init;
    }

    /// <summary>
    /// 是否零复制 GPU 呈现。
    /// </summary>
    public bool IsZeroCopyGpuPresentation
    {
        get; init;
    }

    /// <summary>
    /// GPU 呈现错误。
    /// </summary>
    public string? GpuPresentationError
    {
        get; init;
    }

    /// <summary>
    /// 已接收帧数量。
    /// </summary>
    public long ReceivedFrameCount
    {
        get; init;
    }

    /// <summary>
    /// 已渲染帧数量。
    /// </summary>
    public long RenderedFrameCount
    {
        get; init;
    }

    /// <summary>
    /// 已替换帧数量。
    /// </summary>
    public long ReplacedFrameCount
    {
        get; init;
    }

    /// <summary>
    /// 最近位图上传耗时。
    /// </summary>
    public TimeSpan LastBitmapUploadElapsed
    {
        get; init;
    }

    /// <summary>
    /// 像素大小。
    /// </summary>
    public PixelSize? PixelSize
    {
        get; init;
    }
}
