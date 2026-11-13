using FluxMedia.Core.Models;

namespace FluxMedia.Playback.Internal;

/// <summary>
/// 一次跳转独立等待本代有效首帧。
/// </summary>
internal sealed class PlaybackSeekFrame
{
    /// <summary>
    /// 保护绑定代次与首帧通知一起切换。
    /// </summary>
    private readonly object _sync = new();
    /// <summary>
    /// 本次原生定位完成后允许接收的代次。
    /// </summary>
    private VideoFrameEpoch? _epoch;
    /// <summary>
    /// 本代首帧的完成通知。
    /// </summary>
    private TaskCompletionSource<(TimeSpan Timestamp, TimeSpan Elapsed)> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// 固定请求、目标和开始时间。
    /// </summary>
    public PlaybackSeekFrame(long requestId, TimeSpan target, DateTimeOffset startedAt)
    {
        RequestId = requestId;
        Target = target;
        StartedAt = startedAt;
    }

    /// <summary>
    /// 所属跳转编号。
    /// </summary>
    public long RequestId { get; }
    /// <summary>
    /// 本次目标位置。
    /// </summary>
    public TimeSpan Target { get; }
    /// <summary>
    /// 本次执行开始时间。
    /// </summary>
    public DateTimeOffset StartedAt { get; }
    /// <summary>
    /// 只由本代目标附近帧完成的通知。
    /// </summary>
    public Task<(TimeSpan Timestamp, TimeSpan Elapsed)> FirstFrame { get { lock (_sync) return _completion.Task; } }

    /// <summary>
    /// 定位或重建完成后绑定独立帧代次。
    /// </summary>
    public void BindEpoch(VideoFrameEpoch epoch)
    {
        lock (_sync)
        {
            _epoch = epoch;
            _completion = new TaskCompletionSource<(TimeSpan Timestamp, TimeSpan Elapsed)>(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    /// <summary>
    /// 仅接收本次定位的有效首帧，旧帧不会结束等待。
    /// </summary>
    public bool TryAccept(VideoFrame frame)
    {
        lock (_sync)
        {
            if (_completion.Task.IsCompleted || _epoch is null || !frame.IsCurrent || !frame.IsFromEpoch(_epoch)
                || frame.Timestamp + TimeSpan.FromMilliseconds(40) < Target) return false;
            return _completion.TrySetResult((frame.Timestamp, DateTimeOffset.UtcNow - StartedAt));
        }
    }
}
