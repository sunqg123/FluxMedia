namespace FluxMedia.Core.Models;

/// <summary>
/// DMA-BUF 对象的文件描述符和布局信息。
/// </summary>
/// <param name="FileDescriptor">共享内存对象的文件描述符。</param>
/// <param name="Size">共享对象的总字节数。</param>
/// <param name="FormatModifier">DMA-BUF 的内存布局修饰符。</param>
public sealed record DmaBufObject(int FileDescriptor, ulong Size, ulong FormatModifier);
