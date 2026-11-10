namespace FluxMedia.Playback;

/// <summary>
/// 一次跳转过程中的跟踪记录。
/// </summary>
/// <param name="Timestamp">当前帧或事件的时间戳。</param>
/// <param name="RequestId">当前跳转请求的唯一编号。</param>
/// <param name="Phase">本次跳转当前所处的执行阶段。</param>
/// <param name="Message">当前事件或操作的说明。</param>
/// <param name="Position">当前媒体位置。</param>
/// <param name="Elapsed">当前阶段已经过的时间。</param>
public sealed record PlaybackSeekTraceEntry(DateTimeOffset Timestamp, long RequestId, string Phase, string Message, TimeSpan? Position = null, TimeSpan? Elapsed = null);
