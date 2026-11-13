using LogConverge.Sharp;
using FluxMedia.Playback.Internal;

namespace FluxMedia.Playback;

internal sealed partial class PlaybackSession
{
    /// <summary>
    /// 倍速状态变化通知，不依赖播放进度刷新。
    /// </summary>
    public event Action? PlaybackRateChanged;

    /// <summary>
    /// 最新请求的目标。
    /// </summary>
    public double RequestedPlaybackRate => _rates.Snapshot.RequestedRate;

    /// <summary>
    /// 是否有目标等待应用。
    /// </summary>
    public bool IsPlaybackRatePending => _rates.Snapshot.Pending;

    /// <summary>
    /// 最近一次换速失败。
    /// </summary>
    public string? LastPlaybackRateError => _rates.Snapshot.LastError;

    /// <summary>
    /// 初始设置只作为请求，数值、范围或预算不合法时回退 1 倍。
    /// </summary>
    private void ConfigureInitialRate()
    {
        var requested = _currentOptions.InitialPlaybackRate;
        var valid = PlaybackRatePolicy.TryValidate(requested, _engineOptions,
            Demuxer?.AudioStream is not null, out _, out var error);
        var actual = valid && CanSeekCurrentSource() ? requested : PlaybackRatePolicy.Normal;
        if (!valid || actual != requested)
        {
            Log.Warning(nameof(PlaybackSession),
                $"SessionId={InstanceId} Event=InitialPlaybackRateRejected RequestedRate={requested} MinRate={_engineOptions.MinPlaybackRate} MaxRate={_engineOptions.MaxPlaybackRate} AppliedRate={actual} Budget={_engineOptions.MaxAudioTempoStages} Reason={error ?? "当前媒体不支持倍速"}");
        }
        _rates.Submit(actual, defer: true);
    }

    /// <summary>
    /// 在管线锁外等待提交屏障，兼容 netstandard2.1。
    /// </summary>
    private static async Task WaitForRateBarrierAsync(Task<PlaybackResult> completion, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!completion.IsCompleted)
        {
            var canceled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var registration = cancellationToken.Register(() => canceled.TrySetCanceled(cancellationToken));
            var finished = await Task.WhenAny(completion, canceled.Task).ConfigureAwait(false);
            await finished.ConfigureAwait(false);
        }
        var result = await completion.ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            if (result.Error == PlaybackError.OperationCanceled)
                throw new OperationCanceledException(result.Message);
            throw new InvalidOperationException(result.Message);
        }
    }

    /// <summary>
    /// 通知 Player 读取已提交值或刷新被作废的界面选择。
    /// </summary>
    private void OnPlaybackRateChanged() => PlaybackRateChanged?.Invoke();

    /// <summary>
    /// 看门狗立即取消对应管线，工作线程退出后才释放原生对象。
    /// </summary>
    private void OnRateCommitFaulted(long generation, Exception error)
    {
        var pipeline = Volatile.Read(ref _pipeline);
        if (pipeline is null || pipeline.RateGeneration != generation)
            return;
        pipeline.Fail(error);
        _rates.Retire(preservePending: false);
        // 故障状态迁移另行排队，提交调用栈不获取管线锁。
        _ = Task.Run(() => ReportRateCommitFaultAsync(pipeline, error));
    }

    /// <summary>
    /// 管线故障在普通状态路径报告，提交线程不获取管线锁。
    /// </summary>
    private async Task ReportRateCommitFaultAsync(PlaybackPipeline pipeline, Exception error)
    {
        var entered = false;
        try
        {
            await _pipelineMutationGate.WaitAsync().ConfigureAwait(false);
            entered = true;
            if (!ReferenceEquals(_pipeline, pipeline) || IsDisposed || State is PlaybackState.Stopping or PlaybackState.Stopped or PlaybackState.Failed)
                return;
            await TransitionAsync(PlaybackState.Failed).ConfigureAwait(false);
            NotifyFault(CreateError(PlaybackError.PlayFailed, error.Message), error);
        }
        catch (Exception exception)
        {
            Log.Error(nameof(PlaybackSession),
                $"SessionId={InstanceId} Event=RateCommitFaultReportFailed", exception);
        }
        finally
        {
            if (entered)
                _pipelineMutationGate.Release();
        }
    }
}
