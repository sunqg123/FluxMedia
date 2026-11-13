using FluxMedia.Core.Models;
using FluxMedia.Playback;

namespace FluxMedia.FFmpeg.Pipeline;

internal sealed partial class FFmpegSharedDemuxer
{
    /// <summary>
    /// 按需读取当前任务状态，不保留历史包统计。
    /// </summary>
    public PlaybackNetworkDiagnosticInfo GetNetworkDiagnostic()
    {
        var task = _demuxTask;
        return new PlaybackNetworkDiagnosticInfo
        {
            IsNetworkSource = _source.Kind == MediaSourceKind.NetworkStream,
            ReconnectEnabled = _options.Network.ReconnectEnabled,
            ConfiguredReconnectAttempts = _options.Network.ReconnectEnabled ? _options.NetworkReconnectAttempts : 0,
            DemuxerState = task?.Status.ToString(),
            DemuxerExitReason = task?.IsCompleted == true ? task.Status.ToString() : null,
            LastNetworkError = Volatile.Read(ref _failure)?.Message
        };
    }
}
