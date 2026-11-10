namespace FluxMedia.Core.Models;

/// <summary>
/// 失效后拒绝该代尚未消费的所有视频帧。
/// </summary>
internal sealed class VideoFrameEpoch
{
    /// <summary>
    /// 全进程递增的帧代次编号。
    /// </summary>
    private static long _nextId;

    /// <summary>
    /// 用于关联核心出帧和显示提交日志。
    /// </summary>
    public long Id { get; } = Interlocked.Increment(ref _nextId);

    /// <summary>
    /// 当前代是否仍有效。
    /// </summary>
    private int _valid = 1;

    /// <summary>
    /// 读取跨线程失效状态。
    /// </summary>
    public bool IsCurrent => Volatile.Read(ref _valid) != 0;

    /// <summary>
    /// 一次性废弃当前代。
    /// </summary>
    public void Invalidate() => Interlocked.Exchange(ref _valid, 0);
}
