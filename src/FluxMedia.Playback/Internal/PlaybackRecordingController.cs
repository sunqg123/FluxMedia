using FluxMedia.FFmpeg.Pipeline;

namespace FluxMedia.Playback.Internal;

/// <summary>
/// 管理跨管线的录制意图、转移及文件封装。
/// </summary>
internal sealed class PlaybackRecordingController
{
    /// <summary>
    /// 尚未连接解复用器的录制请求。
    /// </summary>
    private PlaybackRecordingOptions? _pending;
    /// <summary>
    /// 重建期间由会话保管的录制器，重试失败时继续保留。
    /// </summary>
    private FFmpegPacketRemuxRecorder? _detached;
    /// <summary>
    /// 最近一次录制状态。
    /// </summary>
    private PlaybackRecordingDiagnosticInfo _snapshot = new() { State = PlaybackRecordingState.Idle };
    /// <summary>
    /// 是否存在进行中或等待管线的录制请求。
    /// </summary>
    public bool IsRecording { get; private set; }

    /// <summary>
    /// 启动录制；管线尚未建立时保留请求。
    /// </summary>
    public PlaybackResult Start(PlaybackRecordingOptions options, FFmpegSharedDemuxer? demuxer, bool includesAudio)
    {
        if (options is null) return PlaybackResult.Failure(PlaybackError.InvalidState, "录制参数不能为空。");
        if (IsRecording) return PlaybackResult.Success();
        try
        {
            var actual = options with { OutputPath = Path.GetFullPath(options.OutputPath) };
            _snapshot = new PlaybackRecordingDiagnosticInfo
            {
                State = PlaybackRecordingState.Recording,
                OutputPath = actual.OutputPath,
                StartedAt = DateTimeOffset.UtcNow,
                IncludesAudio = includesAudio
            };
            if (demuxer is null) _pending = actual;
            else
            {
                demuxer.StartRecording(actual);
                _snapshot = demuxer.RecordingDiagnostic ?? _snapshot;
            }
            IsRecording = true;
            return PlaybackResult.Success();
        }
        catch (Exception exception)
        {
            _pending = null;
            _snapshot = new PlaybackRecordingDiagnosticInfo
            {
                State = PlaybackRecordingState.Failed,
                OutputPath = options.OutputPath,
                StoppedAt = DateTimeOffset.UtcNow,
                IncludesAudio = includesAudio,
                LastError = exception.Message
            };
            return PlaybackResult.Failure(PlaybackError.RecordingFailed, exception.Message);
        }
    }
    /// <summary>
    /// 重建前接管录制器，保证旧管线释放不会关闭录制文件。
    /// </summary>
    public void Detach(FFmpegSharedDemuxer? demuxer)
    {
        if (!IsRecording || demuxer is null) return;
        if (_detached is not null) throw new InvalidOperationException("录制器尚未完成转移。");
        demuxer.SuspendRecordingForDiscontinuity();
        var recorder = demuxer.DetachRecording();
        if (recorder is null) return;
        _detached = recorder;
        _snapshot = recorder.Diagnostic;
    }
    /// <summary>
    /// 新管线就绪后继续同一录制文件，或执行等待中的录制请求。
    /// </summary>
    public void Attach(FFmpegSharedDemuxer demuxer)
    {
        if (!IsRecording) return;
        if (_detached is not null)
        {
            demuxer.AttachRecording(_detached);
            demuxer.ResumeRecordingAfterDiscontinuity();
            _detached = null;
        }
        else if (_pending is not null)
        {
            demuxer.StartRecording(_pending);
            _pending = null;
        }
    }
    /// <summary>
    /// 停止活动或转移中的录制器，并等待输出封装完成。
    /// </summary>
    public async ValueTask<PlaybackResult> StopAsync(FFmpegSharedDemuxer? demuxer, CancellationToken cancellationToken = default)
    {
        if (!IsRecording) return PlaybackResult.Success();
        try
        {
            if (_detached is not null)
            {
                var recorder = _detached;
                _detached = null;
                await recorder.DisposeAsync().ConfigureAwait(false);
                _snapshot = recorder.Diagnostic;
            }
            else if (demuxer is not null)
                _snapshot = await demuxer.StopRecordingAsync(cancellationToken).ConfigureAwait(false) ?? _snapshot;
            else
                _snapshot = _snapshot with { State = PlaybackRecordingState.Completed, StoppedAt = DateTimeOffset.UtcNow };
            return _snapshot.State == PlaybackRecordingState.Completed                ? PlaybackResult.Success()                : PlaybackResult.Failure(PlaybackError.RecordingFailed, _snapshot.LastError ?? "录制未能正常完成。");
        }
        catch (Exception exception)
        {
            _snapshot = _snapshot with { State = PlaybackRecordingState.Failed, LastError = exception.Message };
            return PlaybackResult.Failure(PlaybackError.RecordingFailed, exception.Message);
        }
        finally { IsRecording = false; _pending = null; }
    }
    /// <summary>
    /// 返回当前活动录制器或最近一次封装状态。
    /// </summary>
    public PlaybackRecordingDiagnosticInfo Snapshot(FFmpegSharedDemuxer? demuxer) =>        IsRecording ? _detached?.Diagnostic ?? demuxer?.RecordingDiagnostic ?? _snapshot : _snapshot;
    /// <summary>
    /// 打开新媒体前清除已结束录制的诊断。
    /// </summary>
    public void Reset()
    {
        if (IsRecording) throw new InvalidOperationException("应先停止旧媒体的录制。");
        _snapshot = new PlaybackRecordingDiagnosticInfo { State = PlaybackRecordingState.Idle };
    }
}
