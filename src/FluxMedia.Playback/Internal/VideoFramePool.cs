using System.Buffers;
using FluxMedia.Core.Models;

namespace FluxMedia.Playback.Internal;

/// <summary>
/// 租用可复用的视频帧缓冲区。
/// </summary>
internal static class VideoFramePool
{
    /// <summary>
    /// 从共享内存池租用指定大小的帧缓冲区。
    /// </summary>
    public static PooledVideoFrameMemoryOwner Rent(int minimumLength)
    {
        return new PooledVideoFrameMemoryOwner(minimumLength);
    }
}

/// <summary>
/// 以引用计数管理池化视频帧内存。
/// </summary>
internal sealed class PooledVideoFrameMemoryOwner : IVideoFrameMemoryOwner
{
    /// <summary>
    /// 当前持有的像素或音频缓冲区。
    /// </summary>
    private byte[]? _buffer;

    /// <summary>
    /// 底层资源当前持有的引用数。
    /// </summary>
    private int _referenceCount = 1;

    /// <summary>
    /// 初始化池化视频帧内存资源所有者所需的状态和资源。
    /// </summary>
    public PooledVideoFrameMemoryOwner(int minimumLength)
    {
        if (minimumLength <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(minimumLength));
        }
        _buffer = ArrayPool<byte>.Shared.Rent(minimumLength);
        Length = minimumLength;
    }

    /// <summary>
    /// 本次租用内存的有效字节数。
    /// </summary>
    public int Length { get; }

    /// <summary>
    /// 尚未归还内存池的有效缓冲区。
    /// </summary>
    public Memory<byte> Memory
    {
        get
        {
            var buffer = _buffer;
            if (buffer is null)
            {
                throw new ObjectDisposedException(nameof(PooledVideoFrameMemoryOwner));
            }
            return buffer.AsMemory(0, Length);
        }
    }

    /// <summary>
    /// 增加底层资源的持有引用。
    /// </summary>
    public void AddReference()
    {
        if (Interlocked.Increment(ref _referenceCount) <= 1)
        {
            throw new ObjectDisposedException(nameof(PooledVideoFrameMemoryOwner));
        }
    }

    /// <summary>
    /// 最后一个引用释放时将缓冲区归还内存池。
    /// </summary>
    public void Release()
    {
        if (Interlocked.Decrement(ref _referenceCount) != 0) return;
        var buffer = Interlocked.Exchange(ref _buffer, null);
        if (buffer is not null)
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
