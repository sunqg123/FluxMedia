using FluxMedia.FFmpeg.Pipeline;

namespace FluxMedia.Playback.Internal;

/// <summary>
/// 持有录制器，管理预约、管线转移和最终结果。
/// </summary>
internal sealed class PlaybackRecordingController
{
    /// <summary>
    /// 等待播放管线的录制预约。
    /// </summary>
    private volatile PlaybackRecordingOptions? _pending;

    /// <summary>
    /// 控制器拥有的录制器，解复用器仅借用它投包。
    /// </summary>
    private volatile FFmpegPacketRemuxRecorder? _recorder;

    /// <summary>
    /// 最近一次录制或预约的结果。
    /// </summary>
    private volatile PlaybackRecordingResult _snapshot = new() { State = PlaybackRecordingState.Idle };

    /// <summary>
    /// 是否还有需要停止或释放的预约、录制器。
    /// </summary>
    public bool HasRecording => _pending is not null || _recorder is not null;

    /// <summary>
    /// 当前录制器的实际状态发生变化。
    /// </summary>
    public event Action? StateChanged;

    /// <summary>
    /// 失败实例先释放，再启动或预约新录制。
    /// </summary>
    public async ValueTask<PlaybackResult> StartAsync(PlaybackRecordingOptions options, FFmpegSharedDemuxer? demuxer)
    {
        if (options is null)
            return PlaybackResult.Failure(PlaybackError.InvalidState, "录制参数不能为空。");
        if (GetResult().State is PlaybackRecordingState.Starting or PlaybackRecordingState.Recording)
            return PlaybackResult.Success();
        try
        {
            var actual = options with
            {
                OutputPath = Path.GetFullPath(options.OutputPath)
            };
            await ReleaseRecorderAsync(demuxer).ConfigureAwait(false);
            _pending = actual;
            _snapshot = new PlaybackRecordingResult
            {
                State = PlaybackRecordingState.Starting,
                OutputPath = actual.OutputPath
            };
            await AttachAsync(demuxer).ConfigureAwait(false);
            return PlaybackResult.Success();
        }
        catch (Exception exception)
        {
            _pending = null;
            _snapshot = new PlaybackRecordingResult
            {
                State = PlaybackRecordingState.Failed,
                OutputPath = options.OutputPath,
                LastError = exception.Message
            };
            return PlaybackResult.Failure(PlaybackError.RecordingFailed, exception.Message);
        }
    }

    /// <summary>
    /// 重建前撤回投包关联，录制器继续由控制器持有。
    /// </summary>
    public void Detach(FFmpegSharedDemuxer? demuxer)
    {
        if (demuxer is null)
            return;
        demuxer.SuspendRecordingForDiscontinuity();
        demuxer.DetachRecording();
    }

    /// <summary>
    /// 新管线借用活动录制器，或启动已有预约。
    /// </summary>
    public async ValueTask AttachAsync(FFmpegSharedDemuxer? demuxer)
    {
        if (demuxer is null)
            return;
        if (_recorder is { } recorder)
        {
            if (recorder.Result.State != PlaybackRecordingState.Recording)
            {
                await ReleaseRecorderAsync(demuxer).ConfigureAwait(false);
                return;
            }
            if (demuxer.AttachRecording(recorder))
                demuxer.ResumeRecordingAfterDiscontinuity();
        }
        else if (_pending is { } options)
        {
            try
            {
                var created = demuxer.CreateRecorder(options);
                _recorder = created;
                created.StateChanged += OnRecorderStateChanged;
                created.Start();
                demuxer.AttachRecording(created);
                _snapshot = created.Result;
            }
            catch (Exception exception)
            {
                try
                {
                    await ReleaseRecorderAsync(demuxer).ConfigureAwait(false);
                }
                catch (Exception cleanup)
                {
                    exception = new AggregateException("录制启动和清理均失败。", exception, cleanup);
                }
                _snapshot = new PlaybackRecordingResult
                {
                    State = PlaybackRecordingState.Failed,
                    OutputPath = options.OutputPath,
                    LastError = exception.Message
                };
                throw new InvalidOperationException($"录制启动失败：{exception.Message}", exception);
            }
            finally
            {
                _pending = null;
            }
        }
    }

    /// <summary>
    /// 接受停止后等待实际收尾，未启动的预约回到空闲。
    /// </summary>
    public async ValueTask<PlaybackResult> StopAsync(FFmpegSharedDemuxer? demuxer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!HasRecording)
            return PlaybackResult.Success();
        if (_recorder is null)
        {
            _pending = null;
            _snapshot = new PlaybackRecordingResult { State = PlaybackRecordingState.Idle };
            return PlaybackResult.Success();
        }
        try
        {
            await ReleaseRecorderAsync(demuxer).ConfigureAwait(false);
            return _snapshot.State == PlaybackRecordingState.Completed ? PlaybackResult.Success()
                : PlaybackResult.Failure(PlaybackError.RecordingFailed, _snapshot.LastError ?? "录制未能正常完成。");
        }
        catch (Exception exception)
        {
            _snapshot = _snapshot with
            {
                State = PlaybackRecordingState.Failed,
                LastError = exception.Message
            };
            return PlaybackResult.Failure(PlaybackError.RecordingFailed, exception.Message);
        }
        finally { _pending = null; }
    }

    /// <summary>
    /// 解除投包关联，等写入线程结束后归还录制器。
    /// </summary>
    private async ValueTask ReleaseRecorderAsync(FFmpegSharedDemuxer? demuxer)
    {
        var recorder = _recorder;
        if (recorder is null)
            return;
        recorder.StateChanged -= OnRecorderStateChanged;
        demuxer?.DetachRecording();
        try
        {
            await recorder.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            _snapshot = recorder.Result;
            _recorder = null;
        }
    }

    /// <summary>
    /// 只转发当前实例的通知，旧录制器不能刷新新录制。
    /// </summary>
    private void OnRecorderStateChanged(FFmpegPacketRemuxRecorder recorder)
    {
        if (ReferenceEquals(recorder, _recorder))
            StateChanged?.Invoke();
    }

    /// <summary>
    /// 读取实际状态，不依赖诊断或文件属性。
    /// </summary>
    public PlaybackRecordingResult GetResult() => _recorder?.Result ?? _snapshot;

    /// <summary>
    /// 新媒体清除已结束的录制结果。
    /// </summary>
    public void Reset()
    {
        if (HasRecording)
            throw new InvalidOperationException("应先停止旧媒体的录制。");
        _snapshot = new PlaybackRecordingResult { State = PlaybackRecordingState.Idle };
    }
}
