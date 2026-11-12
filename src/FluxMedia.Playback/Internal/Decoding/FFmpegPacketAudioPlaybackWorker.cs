using FFmpeg.AutoGen;
using FluxMedia.Core.Configuration;
using FluxMedia.Core.Logging;
using FluxMedia.FFmpeg.Native;
using FluxMedia.FFmpeg.Pipeline;
using FluxMedia.Playback.Internal.Audio;

namespace FluxMedia.Playback.Internal;

/// <summary>
/// 消费音频包并输出解码后的 PCM 数据。
/// </summary>
internal sealed unsafe class FFmpegPacketAudioPlaybackWorker : IDisposable
{
    /// <summary>
    /// 本代媒体信息和实时控制入口。
    /// </summary>
    private readonly PlaybackWorkerContext _context;

    /// <summary>
    /// 当前流的数据包输入。
    /// </summary>
    private readonly FFmpegPacketInput _input;

    /// <summary>
    /// 本工作器的控制与输出回调。
    /// </summary>
    private readonly AudioPlaybackWorkerCallbacks _callbacks;

    /// <summary>
    /// 跳转预滚容差。
    /// </summary>
    private static readonly TimeSpan SeekPrerollTolerance = TimeSpan.FromMilliseconds(10);

    /// <summary>
    /// 输出。
    /// </summary>
    private readonly IAudioOutputDevice _output;

    /// <summary>
    /// 当前管线的音频时间映射。
    /// </summary>
    private readonly AudioPlaybackTimeline _timeline = new();

    /// <summary>
    /// 是否已释放资源。
    /// </summary>
    private volatile bool _disposed;

    /// <summary>
    /// 独立换速请求与确认入口。
    /// </summary>
    private readonly PlaybackRateController _rates;
    /// <summary>
    /// 本工作器所属管线代次。
    /// </summary>
    private readonly long _rateGeneration;
    /// <summary>
    /// 当前媒体允许的滤镜级数。
    /// </summary>
    private readonly int _stageBudget;

    /// <summary>
    /// 绑定音频解码的上下文、输入、设备和回调。
    /// </summary>
    public FFmpegPacketAudioPlaybackWorker(
        PlaybackWorkerContext context,
        FFmpegPacketInput input,
        IAudioOutputDevice output,
        AudioPlaybackWorkerCallbacks callbacks,
        PlaybackRateController rates,
        long rateGeneration,
        int stageBudget)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _input = input ?? throw new ArgumentNullException(nameof(input));
        _callbacks = callbacks ?? throw new ArgumentNullException(nameof(callbacks));
        _output = output ?? throw new ArgumentNullException(nameof(output));
        _rates = rates;
        _rateGeneration = rateGeneration;
        _stageBudget = stageBudget;
    }

    /// <summary>
    /// 在后台运行解码任务。
    /// </summary>
    public Task RunAsync(CancellationToken cancellationToken) => Task.Run(() => DecodeLoop(cancellationToken), CancellationToken.None);

    /// <summary>
    /// 释放当前对象持有的资源。
    /// </summary>
    public void Dispose()
    {
        // 先撤销提交资格，与最后一次 Applied 通知确定唯一先后顺序。
        _rates.AudioExited(_rateGeneration, completed: false, faulted: false);
        _disposed = true;
        _output.Stop();
        FluxMediaLog.Write(LogLevel.Information, nameof(FFmpegPacketAudioPlaybackWorker), _context.InstanceId, $"Event=PacketAudioDispose SourceKind={_context.Source.Kind} Url={_context.Source.Uri}");
    }

    /// <summary>
    /// 仅在暂停状态变化时操作音频设备。
    /// </summary>
    private bool SetOutputPaused(bool paused, ref bool wasPaused)
    {
        if (paused == wasPaused) return false;
        wasPaused = paused;
        if (paused) _output.Pause();
        else _output.Play();
        return true;
    }

    /// <summary>
    /// 读取数据包并循环解码。
    /// </summary>
    private void DecodeLoop(CancellationToken cancellationToken)
    {
        var decodedFrames = 0L;
        var writtenBytes = 0L;
        var currentTime = _context.StartPosition;
        var exitReason = "Completed";
        string? lastError = null;
        var sampleRate = 0;
        var outputChannels = 0;
        var outputFormat = "s16";

        AVCodecContext* codecContext = null;
        AVFrame* frame = null;
        FFmpegAudioTempo? tempo = null;

        try
        {
            var codec = ffmpeg.avcodec_find_decoder(_input.Stream.CodecParameters->codec_id);
            if (codec == null)
            {
                throw new InvalidOperationException("No FFmpeg audio decoder is available for the selected stream.");
            }

            codecContext = ffmpeg.avcodec_alloc_context3(codec);
            if (codecContext == null)
            {
                throw new InvalidOperationException("FFmpeg audio codec context allocation failed.");
            }

            FFmpegErrorHelper.ThrowIfError(ffmpeg.avcodec_parameters_to_context(codecContext, _input.Stream.CodecParameters), "avcodec_parameters_to_context");
            FFmpegErrorHelper.ThrowIfError(ffmpeg.avcodec_open2(codecContext, codec, null), "avcodec_open2");

            sampleRate = codecContext->sample_rate > 0 ? codecContext->sample_rate : 48000;
            outputChannels = codecContext->ch_layout.nb_channels > 0 ? Math.Min((int)codecContext->ch_layout.nb_channels, 2) : 2;
            outputFormat = $"s16/{sampleRate}Hz/{outputChannels}ch";

            tempo = new FFmpegAudioTempo(codecContext, sampleRate, outputChannels, _context.PlaybackRate(), _stageBudget);

            cancellationToken.ThrowIfCancellationRequested();
            if (_disposed) throw new OperationCanceledException();
            _output.Initialize(sampleRate, outputChannels);
            _output.SetVolume(_callbacks.Volume(), _callbacks.IsMuted());
            _output.Play();
            Report(sampleRate, outputChannels, outputFormat, "Running", decodedFrames, writtenBytes, currentTime, null);
            FluxMediaLog.Write(LogLevel.Information, nameof(FFmpegPacketAudioPlaybackWorker), _context.InstanceId, $"Event=PacketAudioStart SourceKind={_context.Source.Kind} Url={_context.Source.Uri} StreamIndex={_input.Stream.StreamIndex} SampleRate={sampleRate} Channels={outputChannels} Decoder={ffmpeg.avcodec_get_name(_input.Stream.CodecParameters->codec_id)}");

            frame = ffmpeg.av_frame_alloc();
            if (frame == null)
            {
                throw new InvalidOperationException("FFmpeg audio frame allocation failed.");
            }

            var wasPaused = false;
            var initialResync = _context.Resync();
            var lastResyncVersion = initialResync.Version;
            var initialPosition = initialResync.Version > 0 ? initialResync.Position : _context.StartPosition;
            currentTime = initialPosition;
            TimeSpan? prerollDropUntil = initialPosition > TimeSpan.Zero ? initialPosition : null;
            // 音频链路②接收：接收滤镜输出的 PCM。
            void WritePcm(ReadOnlySpan<byte> pcm, int samples, double rate)
            {
                if (_disposed || cancellationToken.IsCancellationRequested || _context.Resync().Version != lastResyncVersion) return;
                // 控制预写量，让变速及时生效，并持续响应暂停和 Seek。
                while (_output.IsAvailable && (_context.IsPaused() || _output.GetBufferedDuration() > TimeSpan.FromMilliseconds(150)))
                {
                    if (_disposed || cancellationToken.IsCancellationRequested || _context.Resync().Version != lastResyncVersion) return;
                    var paused = _context.IsPaused();
                    SetOutputPaused(paused, ref wasPaused);
                    Report(sampleRate, outputChannels, outputFormat, paused ? "Paused" : "Running", decodedFrames, writtenBytes, currentTime, null);
                    Thread.Sleep(10);
                }
                if (_disposed || cancellationToken.IsCancellationRequested || _context.Resync().Version != lastResyncVersion) return;
                if (wasPaused && !_context.IsPaused()) SetOutputPaused(false, ref wasPaused);
                // 音频链路②输出：直接写音频设备，不经过视频事件。
                _output.Write(pcm);
                _timeline.Append(samples / (double)sampleRate, rate);
                writtenBytes += pcm.Length;
                Report(sampleRate, outputChannels, outputFormat, "Running", decodedFrames, writtenBytes, currentTime, null);
            }
            TempoPcmConsumer consume = WritePcm;
            var codecHandle = (nint)codecContext;
            // 只在滤镜调用之间处理请求，不能在 WritePcm 内递归排空旧图。
            void ServiceControls()
            {
                if (_disposed || cancellationToken.IsCancellationRequested || _context.IsPaused()
                    || _context.Resync().Version != lastResyncVersion) return;
                SetOutputPaused(false, ref wasPaused);
                tempo = ApplyRateRequest((AVCodecContext*)codecHandle, tempo!, sampleRate, outputChannels, lastResyncVersion, consume);
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (_disposed) throw new OperationCanceledException();
            _rates.AudioReady(_rateGeneration);
            foreach (var packetLease in FFmpegPacketReader.ReadWithDrain(_input.Packets, () => _disposed, cancellationToken,
                ServiceControls, () => _rates.ChangeSignal))
            {
                using (packetLease)
                {
                    ApplyResyncIfNeeded(codecContext, frame, ref tempo, sampleRate, outputChannels, outputFormat, decodedFrames, writtenBytes, ref currentTime, ref lastResyncVersion, ref prerollDropUntil);
                    while (!_disposed && _context.IsPaused() && !cancellationToken.IsCancellationRequested)
                    {
                        ApplyResyncIfNeeded(codecContext, frame, ref tempo, sampleRate, outputChannels, outputFormat, decodedFrames, writtenBytes, ref currentTime, ref lastResyncVersion, ref prerollDropUntil);
                        if (SetOutputPaused(true, ref wasPaused))
                        {
                            Report(sampleRate, outputChannels, outputFormat, "Paused", decodedFrames, writtenBytes, currentTime, null);
                        }
                        Thread.Sleep(20);
                    }
                    if (_disposed || cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }
                    // 同步后只丢弃旧位置的包，保留新位置已经读到的关键包。
                    if (packetLease is not null && packetLease.Generation != _input.PacketGeneration())
                    {
                        FluxMediaLog.Write(LogLevel.Information, nameof(FFmpegPacketAudioPlaybackWorker), _context.InstanceId, $"Event=SeekPacketDiscarded Kind=Audio PacketGeneration={packetLease.Generation} CurrentGeneration={_input.PacketGeneration()}");
                        continue;
                    }

                    SetOutputPaused(false, ref wasPaused);
                    ServiceControls();

                    if (packetLease is not null) _callbacks.PacketConsumed?.Invoke((nint)packetLease.Packet);
                    FFmpegErrorHelper.ThrowIfError(ffmpeg.avcodec_send_packet(codecContext, packetLease is null ? null : packetLease.Packet), "avcodec_send_packet");
                    while (!_disposed && !cancellationToken.IsCancellationRequested)
                    {
                        if (ApplyResyncIfNeeded(codecContext, frame, ref tempo, sampleRate, outputChannels, outputFormat, decodedFrames, writtenBytes, ref currentTime, ref lastResyncVersion, ref prerollDropUntil))
                        {
                            break;
                        }

                        var receive = ffmpeg.avcodec_receive_frame(codecContext, frame);
                        if (receive == ffmpeg.AVERROR(ffmpeg.EAGAIN) || receive == ffmpeg.AVERROR_EOF)
                        {
                            break;
                        }

                        FFmpegErrorHelper.ThrowIfError(receive, "avcodec_receive_frame");
                        currentTime = FrameTimestamp(frame, _input.Stream.TimeBase, _input.Stream.StartTime, decodedFrames, sampleRate);
                        if (ShouldDropPrerollFrame(currentTime, ref prerollDropUntil))
                        {
                            decodedFrames++;
                            ffmpeg.av_frame_unref(frame);
                            FluxMediaLog.Write(LogLevel.Debug, nameof(FFmpegPacketAudioPlaybackWorker), _context.InstanceId, $"Event=PacketAudioPrerollDrop Timestamp={currentTime} Target={prerollDropUntil?.ToString() ?? "-"}");
                            continue;
                        }

                        ServiceControls();
                        var activeTempo = tempo!;
                        _timeline.Start(currentTime, activeTempo.Rate);
                        // 音频链路①绑定：滤镜输出调用上面的 consume。
                        activeTempo.Push(frame, consume);
                        decodedFrames++;
                        ffmpeg.av_frame_unref(frame);
                    }
                }
            }

            // 排空变速滤镜，保留末尾采样。
            if (!_disposed && !cancellationToken.IsCancellationRequested)
                tempo!.Complete(consume);

            // 等待尾音播放完；暂停和停止仍可中断等待。
            while (!_disposed && !cancellationToken.IsCancellationRequested && _output.IsAvailable &&
                _output.GetBufferedDuration() > TimeSpan.Zero)
            {
                var paused = _context.IsPaused();
                SetOutputPaused(paused, ref wasPaused);
                Report(sampleRate, outputChannels, outputFormat, paused ? "Paused" : "Running", decodedFrames, writtenBytes, currentTime, null);
                Thread.Sleep(10);
            }
        }
        catch (OperationCanceledException)
        {
            exitReason = "Canceled";
        }
        catch (Exception ex)
        {
            lastError = ex.Message;
            exitReason = ex.Message;
            FluxMediaLog.Write(LogLevel.Error, nameof(FFmpegPacketAudioPlaybackWorker), _context.InstanceId, $"Event=PacketAudioError SourceKind={_context.Source.Kind} Url={_context.Source.Uri} Error={ex.Message}", ex);
            Report(sampleRate, outputChannels, outputFormat, "Faulted", decodedFrames, writtenBytes, currentTime, lastError);
            throw;
        }
        finally
        {
            var cleanupErrors = new List<Exception>();
            Release(_output.Stop);
            Release(() => tempo?.Dispose());
            if (frame != null)
                ffmpeg.av_frame_free(&frame);
            if (codecContext != null)
                ffmpeg.avcodec_free_context(&codecContext);
            var cleanupFailure = cleanupErrors.Count > 0 ? new AggregateException("音频解码资源释放失败。", cleanupErrors) : null;
            var finalError = lastError ?? cleanupFailure?.Message;
            FluxMediaLog.Write(LogLevel.Information, nameof(FFmpegPacketAudioPlaybackWorker), _context.InstanceId, $"Event=PacketAudioExit SourceKind={_context.Source.Kind} Url={_context.Source.Uri} Reason={finalError ?? exitReason} DecodedFrames={decodedFrames} WrittenPcmBytes={writtenBytes} CurrentTime={currentTime}");
            // 最终状态保留本次错误，避免退出快照覆盖失败信息。
            Report(sampleRate, outputChannels, outputFormat, finalError is null ? "Exited" : "Faulted", decodedFrames, writtenBytes, currentTime, finalError);
            _rates.AudioExited(_rateGeneration, finalError is null && !_disposed && !cancellationToken.IsCancellationRequested,
                faulted: finalError is not null);
            if (lastError is null && cleanupFailure is not null) throw cleanupFailure;

            // 设备停止失败也必须继续归还滤镜和解码器。
            void Release(Action release)
            {
                try { release(); }
                catch (Exception exception)
                {
                    cleanupErrors.Add(exception);
                    FluxMediaLog.Write(LogLevel.Error, nameof(FFmpegPacketAudioPlaybackWorker), _context.InstanceId, "Event=AudioCleanupFailed", exception);
                }
            }
        }
    }

    /// <summary>
    /// 准备候选图，取得提交资格后才排尾音并替换。
    /// </summary>
    private FFmpegAudioTempo ApplyRateRequest(AVCodecContext* codec, FFmpegAudioTempo active,
        int sampleRate, int channels, long resyncVersion, TempoPcmConsumer consume)
    {
        var request = _rates.Peek(_rateGeneration, resyncVersion);
        if (request is null) return active;
        if (request.Rate == active.Rate)
        {
            if (_rates.TryBeginCommit(request, _rateGeneration, resyncVersion))
                _rates.Applied(request, _rateGeneration, resyncVersion);
            return active;
        }
        FFmpegAudioTempo replacement;
        try
        {
            replacement = new FFmpegAudioTempo(codec, sampleRate, channels, request.Rate, _stageBudget, request.Stages);
        }
        catch (Exception error)
        {
            _rates.BuildFailed(request, _rateGeneration, resyncVersion, error);
            return active;
        }
        if (!_rates.TryBeginCommit(request, _rateGeneration, resyncVersion))
        {
            replacement.Dispose();
            return active;
        }
        try
        {
            active.Complete(consume);
            active.Dispose();
        }
        catch (Exception error)
        {
            replacement.Dispose();
            _rates.CommitFailed(request, _rateGeneration, error);
            throw;
        }
        _rates.Applied(request, _rateGeneration, resyncVersion);
        return replacement;
    }

    /// <summary>
    /// 发现新的同步请求时重置解码状态。
    /// </summary>
    private bool ApplyResyncIfNeeded(
        AVCodecContext* codecContext, AVFrame* frame, ref FFmpegAudioTempo? tempo, int sampleRate, int channels, string outputFormat, long decodedFrames, long writtenBytes, ref TimeSpan currentTime, ref long lastResyncVersion, ref TimeSpan? prerollDropUntil)
    {
        var request = _context.Resync();
        if (request.Version == lastResyncVersion)
        {
            return false;
        }

        lastResyncVersion = request.Version;
        ffmpeg.avcodec_flush_buffers(codecContext);
        if (frame != null)
        {
            ffmpeg.av_frame_unref(frame);
        }

        tempo?.Dispose();
        tempo = new FFmpegAudioTempo(codecContext, sampleRate, channels, _context.PlaybackRate(), _stageBudget);
        _timeline.Reset();
        _output.Clear();
        currentTime = request.Position;
        prerollDropUntil = request.Position;
        Report(sampleRate, channels, outputFormat, "SeekResync", decodedFrames, writtenBytes, currentTime, null);
        FluxMediaLog.Write(LogLevel.Information, nameof(FFmpegPacketAudioPlaybackWorker), _context.InstanceId, $"Event=PacketAudioDecoderResync Version={request.Version} Position={request.Position}");
        return true;
    }

    /// <summary>
    /// 判断是否应丢弃目标时间之前的预滚帧。
    /// </summary>
    private static bool ShouldDropPrerollFrame(TimeSpan timestamp, ref TimeSpan? prerollDropUntil)
    {
        var target = prerollDropUntil;
        if (target is null) return false;
        if (timestamp + SeekPrerollTolerance < target.Value) return true;
        prerollDropUntil = null;
        return false;
    }

    /// <summary>
    /// 输出状态。
    /// </summary>
    private string OutputState => _output.IsAvailable ? $"Available:{_output.BackendName}" : $"Unavailable:{_output.LastError ?? "-"}";

    /// <summary>
    /// 上报音频解码和输出状态。
    /// </summary>
    private void Report(int sampleRate, int channels, string outputFormat, string state, long decodedFrames, long writtenBytes, TimeSpan currentTime, string? error)
    {
        var outputDiagnostic = _output.GetDiagnosticInfo();
        // 音频时钟：扣除未播放缓冲和设备延迟，估算可听位置。
        var presentationTime = _timeline.GetPosition(outputDiagnostic.BufferedDuration + outputDiagnostic.DeviceDelay, out var rate);
        // 音频链路③触发：上报状态和时钟，不传递 PCM。
        _callbacks.Diagnostics.Invoke(new AudioPlaybackDiagnostics
        {
            StreamIndex = _input.Stream.StreamIndex,
            SampleRate = sampleRate > 0 ? sampleRate : null,
            Channels = channels > 0 ? channels : null,
            OutputFormat = outputFormat,
            WorkerState = state,
            DecodedFrameCount = decodedFrames,
            WrittenPcmBytes = writtenBytes,
            CurrentTime = currentTime,
            PresentationTime = presentationTime,
            PlaybackRate = rate,
            OutputBufferedDuration = outputDiagnostic.BufferedDuration,
            OutputDeviceDelay = outputDiagnostic.DeviceDelay,
            LastError = error,
            OutputDeviceState = OutputState,
            UpdatedAt = DateTimeOffset.UtcNow
        });
    }

    /// <summary>
    /// 将音频帧时间戳换算为媒体时间。
    /// </summary>
    private static TimeSpan FrameTimestamp(AVFrame* frame, AVRational timeBase, TimeSpan timestampOrigin, long sequence, int sampleRate)
    {
        var pts = frame->best_effort_timestamp;
        if (pts >= 0)
        {
            var seconds = pts * ffmpeg.av_q2d(timeBase);
            if (!double.IsNaN(seconds) && !double.IsInfinity(seconds) && seconds >= 0)
            {
                var timestamp = TimeSpan.FromSeconds(seconds) - timestampOrigin;
                return timestamp < TimeSpan.Zero ? TimeSpan.Zero : timestamp;
            }
        }

        if (sampleRate <= 0)
        {
            return TimeSpan.Zero;
        }

        return TimeSpan.FromSeconds(sequence * frame->nb_samples / (double)sampleRate);
    }
}
