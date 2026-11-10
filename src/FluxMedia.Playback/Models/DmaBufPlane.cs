namespace FluxMedia.Core.Models;

/// <summary>
/// DMA-BUF 图像平面的偏移和行跨度。
/// </summary>
/// <param name="FileDescriptor">共享内存对象的文件描述符。</param>
/// <param name="ObjectIndex">平面引用的 DMA-BUF 对象索引。</param>
/// <param name="Offset">图像平面在共享对象中的字节偏移。</param>
/// <param name="Pitch">图像平面每行占用的字节数。</param>
public sealed record DmaBufPlane(int FileDescriptor, int ObjectIndex, long Offset, long Pitch);
