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
