using FluxMedia.Core.Models;
using FluxMedia.Playback.Internal;

namespace FluxMedia.Playback;

internal sealed partial class PlaybackSession
{
    /// <summary>
    /// 仅保护诊断参数描述的缓存。
    /// </summary>
    private readonly object _diagnosticDescriptionSync = new();

    /// <summary>
    /// 最近一次查询的媒体、配置和参数描述。
    /// </summary>
    private (MediaSource Source, PlaybackOptions Options, string Text)? _diagnosticDescription;

    /// <summary>
    /// 按需读取同一媒体的事实，切换中无法稳定读取时返回空值。
    /// </summary>
    public PlaybackDiagnosticInfo? GetDiagnosticInfo()
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var state = State;
            if (state is PlaybackState.Opening or PlaybackState.Stopping)
                return null;
            var operation = Volatile.Read(ref _operationCts);
            var synchronization = _resyncSignal.Snapshot();
            var pipeline = Volatile.Read(ref _pipeline);
            var source = CurrentSource;
            var info = CurrentMediaInfo;
            var options = Volatile.Read(ref _currentOptions);
            var disposed = IsDisposed;
            if (pipeline is not null && !pipeline.MatchesDiagnosticMedia(source, options))
                continue;

            var rate = _rates.Snapshot;
            var seek = _seeking.Operation;
            var seekFrame = Volatile.Read(ref _activeSeek);
            var clock = pipeline?.AudioClock;
            var audio = clock?.IsRunning == true ? clock : null;
            var timing = pipeline?.Demuxer.MediaInfo is null ? (PlaybackTimingMode?)null : pipeline.Demuxer.TimingMode;
            var position = CurrentTime;
            var videoBuffered = pipeline?.Demuxer.VideoBufferedDuration;
            TimeSpan? duration = info?.Duration > TimeSpan.Zero ? info.Duration : null;
            var diagnostic = new PlaybackDiagnosticInfo
            {
                State = state,
                SourceKind = source?.Kind,
                SourceUrl = source?.Uri,
                FFmpegOpenOptions = source is null ? null : ReadFFmpegOptionsDescription(source, options),
                Protocol = MediaSourcePolicy.GetProtocol(source),
                Container = info?.ContainerFormat,
                FrameRate = info?.VideoFrameRate,
                TimingMode = timing,
                LowLatencyEnabled = timing.HasValue ? timing == PlaybackTimingMode.RealtimeLowLatency : null,
                VideoBufferedDuration = videoBuffered,
                HardwareAccelerationRequested = options.VideoAcceleration != VideoAccelerationMode.Disabled,
                CanSeek = CanSeek,
                IsLive = IsLive,
                HasDuration = duration.HasValue,
                CurrentTime = position,
                Duration = duration,
                PlaybackRate = rate.AppliedRate,
                RequestedRate = rate.RequestedRate,
                AppliedRate = rate.AppliedRate,
                MinRate = rate.MinRate,
                MaxRate = rate.MaxRate,
                RateRequestId = rate.RequestId,
                RateChangeState = rate.State,
                IsSpeedChangePending = rate.Pending,
                LastRateError = rate.LastError,
                Volume = Volume,
                IsMuted = IsMuted,
                AudioOutputAvailable = pipeline?.AudioOutputAvailable == true,
                AudioStreamIndex = pipeline?.Demuxer.AudioStream?.StreamIndex,
                AudioSampleRate = info?.AudioSampleRate,
                AudioChannels = info?.AudioChannels,
                AudioCurrentTime = audio?.PresentationTime,
                VideoCurrentTime = position,
                AudioOutputDeviceState = pipeline?.ReadAudioDeviceState(),
                Health = new PlaybackHealthDiagnosticInfo
                {
                    IsHealthy = state == PlaybackState.Failed ? false : null,
                    Reason = state == PlaybackState.Failed ? "PlaybackFailed" : null,
                    VideoBufferedDuration = videoBuffered,
                    AudioBufferedDuration = audio?.BufferedDuration,
                    AudioDeviceDelay = audio?.DeviceDelay,
                    CheckedAt = DateTimeOffset.UtcNow
                },
                Network = pipeline?.Demuxer.GetNetworkDiagnostic() ?? new PlaybackNetworkDiagnosticInfo
                {
                    IsNetworkSource = source?.Kind == MediaSourceKind.NetworkStream,
                    ReconnectEnabled = options.Network.ReconnectEnabled,
                    ConfiguredReconnectAttempts = options.Network.ReconnectEnabled ? options.NetworkReconnectAttempts : 0
                },
                LastSeek = ReadSeekDiagnostic(seek, seekFrame, state)
            };
            if (IsDiagnosticContextCurrent(pipeline, source, info, options, operation, synchronization, state)
                && disposed == IsDisposed && ReferenceEquals(seek, _seeking.Operation)
                && ReferenceEquals(seekFrame, Volatile.Read(ref _activeSeek)))
                return diagnostic;
        }
        return null;
    }

    /// <summary>
    /// 同一媒体和配置复用描述，不参与播放流程。
    /// </summary>
    private string ReadFFmpegOptionsDescription(MediaSource source, PlaybackOptions options)
    {
        lock (_diagnosticDescriptionSync)
        {
            if (_diagnosticDescription is { } cached && ReferenceEquals(cached.Source, source)
                && ReferenceEquals(cached.Options, options))
                return cached.Text;
            var text = PlaybackFFmpegOptions.Describe(source, options);
            _diagnosticDescription = (source, options, text);
            return text;
        }
    }

    /// <summary>
    /// 校验已有操作范围和媒体身份，不等待播放命令锁。
    /// </summary>
    private bool IsDiagnosticContextCurrent(PlaybackPipeline? pipeline, MediaSource? source, MediaInfo? info,
        PlaybackOptions options, CancellationTokenSource? operation, PlaybackResyncRequest synchronization, PlaybackState state)
    {
        return synchronization == _resyncSignal.Snapshot()
            && ReferenceEquals(operation, Volatile.Read(ref _operationCts))
            && ReferenceEquals(pipeline, Volatile.Read(ref _pipeline))
            && ReferenceEquals(source, CurrentSource)
            && ReferenceEquals(info, CurrentMediaInfo)
            && ReferenceEquals(options, Volatile.Read(ref _currentOptions))
            && state == State;
    }

    /// <summary>
    /// 从本次捕获的执行状态和首帧任务读取跳转信息。
    /// </summary>
    private PlaybackSeekDiagnosticInfo ReadSeekDiagnostic(PlaybackSeekState operation, PlaybackSeekFrame? seek, PlaybackState state)
    {
        var matching = seek?.RequestId == operation.RequestId;
        var firstFrame = matching ? seek?.FirstFrame : null;
        return new PlaybackSeekDiagnosticInfo
        {
            RequestId = operation.RequestId,
            RequestedPosition = matching ? seek?.Target : null,
            ActualPosition = firstFrame?.IsCompletedSuccessfully == true ? firstFrame.Result.Timestamp : null,
            Path = operation.Path == PlaybackSeekPath.None ? null : operation.Path.ToString(),
            InProgress = state == PlaybackState.Seeking,
            CoalescedRequestCount = operation.CoalescedRequestCount,
            NextCoalesceDelay = _seeking.CoalesceDelay,
            DemuxerSeekElapsed = operation.DemuxerSeekElapsed,
            TotalElapsed = operation.TotalElapsed,
            FirstUiFrameElapsed = operation.FirstFrameElapsed,
            FirstUiRecoveryAttempted = operation.RecoveryAttempted,
            StartedAt = matching ? seek?.StartedAt : null
        };
    }

    /// <summary>
    /// 按需读取录制及文件信息，媒体切换使本次结果作废。
    /// </summary>
    public PlaybackRecordingDiagnosticInfo? GetRecordingDiagnosticInfo()
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var state = State;
            if (state is PlaybackState.Opening or PlaybackState.Stopping)
                return null;
            var operation = Volatile.Read(ref _operationCts);
            var synchronization = _resyncSignal.Snapshot();
            var pipeline = Volatile.Read(ref _pipeline);
            var source = CurrentSource;
            var info = CurrentMediaInfo;
            var options = Volatile.Read(ref _currentOptions);
            if (pipeline is not null && !pipeline.MatchesDiagnosticMedia(source, options))
                continue;

            var result = _recording.GetResult();
            var diagnostic = new PlaybackRecordingDiagnosticInfo
            {
                State = result.State,
                OutputPath = result.OutputPath,
                EncodedFrameCount = result.WrittenPackets,
                OutputFileFinalized = result.OutputFileFinalized,
                LastError = result.LastError,
                VideoCodec = result.OutputPath is null ? null : info?.VideoCodec,
                IncludesAudio = result.OutputPath is null ? null : info?.HasAudio
            };
            if (!string.IsNullOrWhiteSpace(result.OutputPath))
            {
                try
                {
                    var file = new FileInfo(result.OutputPath);
                    var exists = file.Exists;
                    diagnostic = diagnostic with
                    {
                        OutputFileExists = exists,
                        OutputFileSize = exists ? file.Length : null
                    };
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException
                    or System.Security.SecurityException or ArgumentException or NotSupportedException)
                {
                    // 文件属性不可读时保留录制结果，不修改播放状态。
                }
            }
            if (IsDiagnosticContextCurrent(pipeline, source, info, options, operation, synchronization, state))
                return diagnostic;
        }
        return null;
    }
}
