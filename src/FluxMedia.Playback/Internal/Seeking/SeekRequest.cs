namespace FluxMedia.Playback.Internal;

/// <summary>
/// 一次合并跳转及其所属的操作生命周期。
/// </summary>
/// <param name="Target">跳转目标。</param>
/// <param name="ResumePlaying">是否继续播放。</param>
/// <param name="Id">请求标识。</param>
/// <param name="FirstQueuedAt">首次入队时间。</param>
/// <param name="Count">本组合并次数。</param>
/// <param name="Token">入队时捕获的操作令牌。</param>
internal sealed record SeekRequest(TimeSpan Target, bool ResumePlaying, long Id, DateTimeOffset FirstQueuedAt, int Count, CancellationToken Token);
