using FluxMedia.Core.Configuration;
using FluxMedia.Core.Logging;
using FluxMedia.Playback;

namespace FluxMedia.FFmpeg.Pipeline;

// 录制异步收尾独立于原生指针的 unsafe 作用域。
internal sealed partial class FFmpegSharedDemuxer
{
    /// <summary>
    /// 等待录制排空、封装并释放，再解除关联。
    /// </summary>
    public async ValueTask<PlaybackRecordingDiagnosticInfo?> StopRecordingAsync()
    {
        FFmpegPacketRemuxRecorder? recording;
        lock (_recordingSync)
        {
            recording = _recording;
        }
        if (recording is null) return null;

        try
        {
            Exception? failure = null;
            try { await recording.DisposeAsync().ConfigureAwait(false); }
            catch (Exception exception)
            {
                failure = exception;
                FluxMediaLog.Write(LogLevel.Error, nameof(FFmpegSharedDemuxer), _instanceId, "Event=RecordingCleanupFailed", exception);
            }
            var diagnostic = recording.Diagnostic;
            return failure is null ? diagnostic : diagnostic with
            {
                State = PlaybackRecordingState.Failed,
                StoppedAt = diagnostic.StoppedAt ?? DateTimeOffset.UtcNow,
                LastError = failure.Message
            };
        }
        finally
        {
            lock (_recordingSync)
            {
                if (ReferenceEquals(_recording, recording)) _recording = null;
            }
        }
    }
}
