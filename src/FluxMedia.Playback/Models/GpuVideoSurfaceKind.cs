namespace FluxMedia.Core.Models;

/// <summary>
/// GPU 视频帧的共享方式。
/// </summary>
public enum GpuVideoSurfaceKind
{
    /// <summary>
    /// D3D11 共享纹理。
    /// </summary>
    D3D11SharedTexture,
    /// <summary>
    /// DMA-BUF。
    /// </summary>
    DmaBuf
}
