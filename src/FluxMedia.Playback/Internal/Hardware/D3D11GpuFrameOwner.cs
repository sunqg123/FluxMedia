using FluxMedia.Core.Models;

namespace FluxMedia.Playback.Internal.Hardware;

/// <summary>
/// 通过引用计数归还 D3D11 视频表面。
/// </summary>
internal sealed class D3D11GpuFrameOwner : IVideoFrameOwner
{
    /// <summary>
    /// 池。
    /// </summary>
    private D3D11VideoProcessor? _pool;

    /// <summary>
    /// 当前持有的 GPU 显示表面。
    /// </summary>
    private D3D11SharedSurface? _surface;

    /// <summary>
    /// 底层资源当前持有的引用数。
    /// </summary>
    private int _referenceCount = 1;

    /// <summary>
    /// 初始化 D3D11GPU 帧资源所有者所需的状态和资源。
    /// </summary>
    public D3D11GpuFrameOwner(D3D11VideoProcessor pool, D3D11SharedSurface surface)
    {
        _pool = pool;
        _surface = surface;
    }

    /// <summary>
    /// 共享句柄。
    /// </summary>
    public nint SharedHandle => _surface?.SharedHandle ?? throw new ObjectDisposedException(nameof(D3D11GpuFrameOwner));

    /// <summary>
    /// 增加底层资源的持有引用。
    /// </summary>
    public void AddReference()
    {
        while (true)
        {
            var current = Volatile.Read(ref _referenceCount);
            if (current <= 0)
            {
                throw new ObjectDisposedException(nameof(D3D11GpuFrameOwner));
            }

            if (Interlocked.CompareExchange(ref _referenceCount, current + 1, current) == current) return;
        }
    }

    /// <summary>
    /// 释放一个资源引用，最后一个引用负责清理。
    /// </summary>
    public void Release()
    {
        if (Interlocked.Decrement(ref _referenceCount) != 0) return;
        var pool = Interlocked.Exchange(ref _pool, null);
        var surface = Interlocked.Exchange(ref _surface, null);
        if (pool is not null && surface is not null)
        {
            pool.Return(surface);
        }
    }
}
