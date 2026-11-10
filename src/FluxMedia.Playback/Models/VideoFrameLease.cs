namespace FluxMedia.Core.Models;

/// <summary>
/// 保留视频帧，释放租约后归还引用。
/// </summary>
public sealed class VideoFrameLease : IDisposable
{
    /// <summary>
    /// 是否已释放资源。
    /// </summary>
    private int _disposed;

    /// <summary>
    /// 初始化视频帧租约所需的状态和资源。
    /// </summary>
    internal VideoFrameLease(VideoFrame frame)
    {
        Frame = frame;
    }

    /// <summary>
    /// 帧。
    /// </summary>
    public VideoFrame Frame { get; }

    /// <summary>
    /// 释放当前对象持有的资源。
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            Frame.ReleaseLease();
        }
    }
}
