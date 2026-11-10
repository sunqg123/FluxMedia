namespace FluxMedia.Core.Models;

/// <summary>
/// 提供可复用的视频帧内存。
/// </summary>
internal interface IVideoFrameMemoryOwner : IVideoFrameOwner
{
    /// <summary>
    /// 内存。
    /// </summary>
    Memory<byte> Memory { get; }
}
