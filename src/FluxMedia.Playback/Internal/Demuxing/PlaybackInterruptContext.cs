using FFmpeg.AutoGen;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace FluxMedia.Playback.Internal;

/// <summary>
/// 通过 FFmpeg 回调中断取消或超时的读取。
/// </summary>
internal sealed unsafe class PlaybackInterruptContext : IDisposable
{
    /// <summary>
    /// 取消令牌。
    /// </summary>
    private readonly CancellationToken _cancellationToken;

    /// <summary>
    /// 传给 FFmpeg 的中断回调。
    /// </summary>
    private readonly AVIOInterruptCB_callback _callback;

    /// <summary>
    /// 句柄。
    /// </summary>
    private GCHandle _handle;

    /// <summary>
    /// 超时。
    /// </summary>
    private TimeSpan _timeout;

    /// <summary>
    /// 当前原生操作的超时截止刻度。
    /// </summary>
    private long _deadlineTicks;

    /// <summary>
    /// 是否已释放资源。
    /// </summary>
    private bool _disposed;

    /// <summary>
    /// 初始化播放中断上下文所需的状态和资源。
    /// </summary>
    public PlaybackInterruptContext(TimeSpan timeout, CancellationToken cancellationToken)
    {
        _timeout = timeout <= TimeSpan.Zero ? TimeSpan.FromSeconds(5) : timeout;
        _cancellationToken = cancellationToken;
        _callback = Interrupt;
        _handle = GCHandle.Alloc(this);
        ResetDeadline();
    }

    /// <summary>
    /// 回调。
    /// </summary>
    public AVIOInterruptCB Callback => new()
    {
        callback = _callback,
        opaque = (void*)GCHandle.ToIntPtr(_handle)
    };

    /// <summary>
    /// 是否已超时输出。
    /// </summary>
    public bool TimedOut => Stopwatch.GetTimestamp() > Interlocked.Read(ref _deadlineTicks);

    /// <summary>
    /// 按当前超时重新计算截止时间。
    /// </summary>
    public void ResetDeadline()
    {
        var ticks = Stopwatch.GetTimestamp() + (long)(_timeout.TotalSeconds * Stopwatch.Frequency);
        Interlocked.Exchange(ref _deadlineTicks, ticks);
    }

    /// <summary>
    /// 更新超时时间并重置截止时间。
    /// </summary>
    public void SetTimeout(TimeSpan timeout)
    {
        _timeout = timeout <= TimeSpan.Zero ? TimeSpan.FromSeconds(5) : timeout;
        ResetDeadline();
    }

    /// <summary>
    /// 释放当前对象持有的资源。
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_handle.IsAllocated)
        {
            _handle.Free();
        }
    }

    /// <summary>
    /// 向 FFmpeg 返回是否应中断当前操作。
    /// </summary>
    private static int Interrupt(void* opaque)
    {
        if (opaque == null)
        {
            return 0;
        }

        var handle = GCHandle.FromIntPtr((nint)opaque);
        if (handle.Target is not PlaybackInterruptContext context)
        {
            return 0;
        }

        return context._cancellationToken.IsCancellationRequested || context.TimedOut ? 1 : 0;
    }
}
