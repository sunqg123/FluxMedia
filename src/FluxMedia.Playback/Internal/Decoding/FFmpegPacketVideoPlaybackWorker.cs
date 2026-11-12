using FFmpeg.AutoGen;
using FluxMedia.Core.Configuration;
using FluxMedia.Core.Logging;
using FluxMedia.Core.Models;
using FluxMedia.FFmpeg.Native;
using FluxMedia.FFmpeg.Pipeline;
using FluxMedia.Playback.Internal.Hardware;
using System.Diagnostics;
using System.Runtime.InteropServices;
using CorePixelFormat = FluxMedia.Core.Models.PixelFormat;

namespace FluxMedia.Playback.Internal;

/// <summary>
/// 消费视频包并解码、调度和输出视频帧。
/// </summary>
internal sealed unsafe class FFmpegPacketVideoPlaybackWorker : IDisposable
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
    private readonly VideoPlaybackWorkerCallbacks _callbacks;

    /// <summary>
    /// 跳转预滚容差。
    /// </summary>
    private static readonly TimeSpan SeekPrerollTolerance = TimeSpan.FromMilliseconds(15);

    /// <summary>
    /// 配置。
    /// </summary>
    private readonly PlaybackOptions _options;

    /// <summary>
    /// 是否硬件加速请求。
    /// </summary>
    private bool _hardwareAccelerationRequested;

    /// <summary>
    /// 是否已启用硬件加速。
    /// </summary>
    private bool _hardwareAccelerationActive;

    /// <summary>
    /// 视频解码器后端。
    /// </summary>
    private string _videoDecoderBackend = "Software";

    /// <summary>
    /// 视频呈现后端。
    /// </summary>
    private string _videoPresentationBackend = "CPU BGRA";

    /// <summary>
    /// 硬件加速失败原因。
    /// </summary>
    private string? _hardwareAccelerationFailureReason;

    /// <summary>
    /// 是否已释放资源。
    /// </summary>
    private volatile bool _disposed;

    /// <summary>
    /// 退役时失效，阻止旧管线的帧继续显示。
    /// </summary>
    private readonly VideoFrameEpoch _producerEpoch = new();

    /// <summary>
    /// 当前解码状态对应的跳转帧代次。
    /// </summary>
    private VideoFrameEpoch? _frameEpoch;

    /// <summary>
    /// 绑定视频解码的上下文、输入、配置和回调。
    /// </summary>
    public FFmpegPacketVideoPlaybackWorker(
        PlaybackWorkerContext context,
        FFmpegPacketInput input,
        PlaybackOptions options,
        VideoPlaybackWorkerCallbacks callbacks)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _input = input ?? throw new ArgumentNullException(nameof(input));
        _callbacks = callbacks ?? throw new ArgumentNullException(nameof(callbacks));
        _options = options ?? throw new ArgumentNullException(nameof(options));
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
        _disposed = true;
        _producerEpoch.Invalidate();
        FluxMediaLog.Write(LogLevel.Information, nameof(FFmpegPacketVideoPlaybackWorker), _context.InstanceId, $"Event=PacketVideoWorkerDispose SourceKind={_context.Source.Kind} Url={_context.Source.Uri}");
    }

    /// <summary>
    /// 读取数据包并循环解码。
    /// </summary>
    private void DecodeLoop(CancellationToken cancellationToken)
    {
        var workerStartedAt = DateTimeOffset.UtcNow;
        var timingMode = _context.TimingMode;
        var firstFrameLogged = false;
        TimeSpan? firstDecodedFrameElapsed = null;
        var readPackets = 0L;
        var decodedFrames = 0L;
        var pushedFrames = 0L;
        var droppedVideoFrames = 0L;
        var bufferingDecisionCount = 0L;
        TimeSpan? lastVideoAudioDistance = null;
        TimeSpan? lastAudioClock = null;
        TimeSpan? lastVideoBufferedDuration = null;
        var lowLatencyCatchUpActive = false;
        var lastFrameAt = DateTimeOffset.MinValue;
        var exitReason = "Completed";
        string? lastError = null;

        AVCodecContext* codecContext = null;
        AVFrame* frame = null;
        AVFrame* softwareFrame = null;
        SwsContext* swsContext = null;
        D3D11HardwareVideoContext? d3d11Hardware = null;
        VaapiHardwareVideoContext? vaapiHardware = null;

        try
        {
            var codec = ffmpeg.avcodec_find_decoder(_input.Stream.CodecParameters->codec_id);
            if (codec == null)
            {
                throw new InvalidOperationException("No FFmpeg video decoder is available for the selected stream.");
            }

            codecContext = ffmpeg.avcodec_alloc_context3(codec);
            if (codecContext == null)
            {
                throw new InvalidOperationException("FFmpeg video codec context allocation failed.");
            }

            FFmpegErrorHelper.ThrowIfError(ffmpeg.avcodec_parameters_to_context(codecContext, _input.Stream.CodecParameters), "avcodec_parameters_to_context");
            var capabilities = _callbacks.VideoCapabilities();
            _hardwareAccelerationRequested = _options.VideoAcceleration != VideoAccelerationMode.Disabled;
            string? d3d11Error = null;
            string? vaapiError = null;
            var hardwareInitStartedAt = Stopwatch.GetTimestamp();
            if (_options.VideoAcceleration != VideoAccelerationMode.Disabled && capabilities.SupportsD3D11SharedTexture && D3D11HardwareVideoContext.TryCreate(codec, capabilities.AdapterLuid, _context.InstanceId, out d3d11Hardware, out d3d11Error))
            {
                d3d11Hardware!.ConfigureCodec(codecContext);
                _hardwareAccelerationActive = true;
                _videoDecoderBackend = "D3D11VA";
                _videoPresentationBackend = "D3D11 shared BGRA texture";
            }
            else if (_options.VideoAcceleration != VideoAccelerationMode.Disabled && RuntimeInformation.IsOSPlatform(OSPlatform.Linux) && VaapiHardwareVideoContext.TryCreate(codec, _context.InstanceId, out vaapiHardware, out vaapiError))
            {
                vaapiHardware!.ConfigureCodec(codecContext);
                _hardwareAccelerationActive = true;
                _videoDecoderBackend = "VAAPI";
                _videoPresentationBackend = "CPU BGRA (VAAPI download)";
                _hardwareAccelerationFailureReason = capabilities.SupportsVulkanOpaqueFileDescriptor
                    ? "The VAAPI DMA-BUF to Vulkan opaque-FD bridge is not available; using CPU presentation."
                    : "The active Avalonia backend cannot import Vulkan opaque file descriptors; using CPU presentation.";
            }
            else if (_options.VideoAcceleration == VideoAccelerationMode.RequireHardware)
            {
                throw new InvalidOperationException(d3d11Error ?? vaapiError ?? "No compatible hardware video output was configured.");
            }
            else if (_hardwareAccelerationRequested)
            {
                _hardwareAccelerationFailureReason = d3d11Error ?? vaapiError ?? "The active video output cannot import D3D11 shared textures.";
            }

            var hardwareInitElapsed = PlaybackClock.ElapsedSince(hardwareInitStartedAt);
            FluxMediaLog.Write(
                LogLevel.Information, nameof(FFmpegPacketVideoPlaybackWorker), _context.InstanceId, $"Event=PacketVideoHardwareInitCompleted SourceKind={_context.Source.Kind} Url={_context.Source.Uri} Requested={_hardwareAccelerationRequested} Active={_hardwareAccelerationActive} DecoderBackend={_videoDecoderBackend} PresentationBackend={_videoPresentationBackend} Failure={_hardwareAccelerationFailureReason ?? "-"} ElapsedMs={hardwareInitElapsed.TotalMilliseconds:0}");

            FluxMediaLog.Write(
                LogLevel.Information, nameof(FFmpegPacketVideoPlaybackWorker), _context.InstanceId, $"Event=PacketVideoCodecOpenStart SourceKind={_context.Source.Kind} Url={_context.Source.Uri} Decoder={ffmpeg.avcodec_get_name(_input.Stream.CodecParameters->codec_id)} Backend={_videoDecoderBackend}");
            var codecOpenStartedAt = Stopwatch.GetTimestamp();
            var codecOpenResult = ffmpeg.avcodec_open2(codecContext, codec, null);
            var codecOpenElapsed = PlaybackClock.ElapsedSince(codecOpenStartedAt);
            FluxMediaLog.Write(
                codecOpenResult < 0 ? LogLevel.Warning : LogLevel.Information, nameof(FFmpegPacketVideoPlaybackWorker), _context.InstanceId, $"Event=PacketVideoCodecOpenCompleted SourceKind={_context.Source.Kind} Url={_context.Source.Uri} Backend={_videoDecoderBackend} Result={codecOpenResult} Error={FormatError(codecOpenResult) ?? "-"} ElapsedMs={codecOpenElapsed.TotalMilliseconds:0}");
            if (codecOpenResult < 0 && (d3d11Hardware is not null || vaapiHardware is not null) && _options.VideoAcceleration == VideoAccelerationMode.Auto)
            {
                var failedBackend = d3d11Hardware is not null ? "D3D11VA" : "VAAPI";
                FluxMediaLog.Write(
                    LogLevel.Warning, nameof(FFmpegPacketVideoPlaybackWorker), _context.InstanceId, $"Event=HardwareDecoderOpenFallback Backend={failedBackend} Error={FFmpegErrorHelper.GetErrorMessage(codecOpenResult)}");
                d3d11Hardware?.Dispose();
                d3d11Hardware = null;
                vaapiHardware?.Dispose();
                vaapiHardware = null;
                _hardwareAccelerationActive = false;
                _videoDecoderBackend = "Software";
                _videoPresentationBackend = "CPU BGRA";
                _hardwareAccelerationFailureReason = $"{failedBackend} decoder open failed: {FFmpegErrorHelper.GetErrorMessage(codecOpenResult)}";
                ffmpeg.avcodec_free_context(&codecContext);
                codecContext = ffmpeg.avcodec_alloc_context3(codec);
                if (codecContext == null)
                {
                    throw new InvalidOperationException("FFmpeg video codec context allocation failed during hardware fallback.");
                }

                FFmpegErrorHelper.ThrowIfError(
                    ffmpeg.avcodec_parameters_to_context(codecContext, _input.Stream.CodecParameters), "avcodec_parameters_to_context");
                FluxMediaLog.Write(
                    LogLevel.Information, nameof(FFmpegPacketVideoPlaybackWorker), _context.InstanceId, $"Event=PacketVideoCodecOpenStart SourceKind={_context.Source.Kind} Url={_context.Source.Uri} Decoder={ffmpeg.avcodec_get_name(_input.Stream.CodecParameters->codec_id)} Backend={_videoDecoderBackend}");
                codecOpenStartedAt = Stopwatch.GetTimestamp();
                codecOpenResult = ffmpeg.avcodec_open2(codecContext, codec, null);
                codecOpenElapsed = PlaybackClock.ElapsedSince(codecOpenStartedAt);
                FluxMediaLog.Write(
                    codecOpenResult < 0 ? LogLevel.Warning : LogLevel.Information, nameof(FFmpegPacketVideoPlaybackWorker), _context.InstanceId, $"Event=PacketVideoCodecOpenCompleted SourceKind={_context.Source.Kind} Url={_context.Source.Uri} Backend={_videoDecoderBackend} Result={codecOpenResult} Error={FormatError(codecOpenResult) ?? "-"} ElapsedMs={codecOpenElapsed.TotalMilliseconds:0}");
            }

            FFmpegErrorHelper.ThrowIfError(codecOpenResult, "avcodec_open2");

            frame = ffmpeg.av_frame_alloc();
            if (frame == null)
            {
                throw new InvalidOperationException("FFmpeg video frame allocation failed.");
            }

            var width = Math.Max(codecContext->width, 1);
            var height = Math.Max(codecContext->height, 1);
            var sourcePixelFormat = AVPixelFormat.AV_PIX_FMT_NONE;
            var sequence = 0L;
            var frameRate = FrameRateCalculator.FromRationalPair(_input.Stream.AverageFrameRate.num, _input.Stream.AverageFrameRate.den, _input.Stream.RealFrameRate.num, _input.Stream.RealFrameRate.den);
            var fallbackFrameInterval = TimeSpan.FromSeconds(1d / Math.Max(frameRate, 25d));
            var initialResync = _context.Resync();
            var lastResyncVersion = initialResync.Version;
            _frameEpoch = initialResync.FrameEpoch;
            var initialPosition = initialResync.Version > 0 ? initialResync.Position : _context.StartPosition;
            TimeSpan? prerollDropUntil = initialPosition > TimeSpan.Zero ? initialPosition : null;
            var scheduler = new PlaybackScheduler(timingMode, _context.PlaybackRate, () => _disposed || _context.Resync().Version != lastResyncVersion, _callbacks.AudioDiagnostics, _callbacks.BufferedDuration, _callbacks.BufferedPacketCount, minBufferDuration: _context.Source.Kind == MediaSourceKind.File ? TimeSpan.Zero : TimeSpan.FromMilliseconds(500), maxLowLatencyDelay: _options.Network.MaxNetworkFrameDelay);
            var lastTimestamp = TimeSpan.Zero;
            var lastStatsAt = DateTimeOffset.UtcNow;
            if (initialPosition > TimeSpan.Zero) scheduler.Reset();
            var loggedPacketGeneration = -1L;
            var wasPaused = false;

            ReportDiagnostics(running: true, frameRate: frameRate);
            FluxMediaLog.Write(LogLevel.Information, nameof(FFmpegPacketVideoPlaybackWorker), _context.InstanceId, $"Event=PacketVideoWorkerStart SourceKind={_context.Source.Kind} Url={_context.Source.Uri} StreamIndex={_input.Stream.StreamIndex} Decoder={ffmpeg.avcodec_get_name(_input.Stream.CodecParameters->codec_id)} FrameRate={frameRate:0.###}");

            foreach (var packetLease in FFmpegPacketReader.ReadWithDrain(_input.Packets, () => _disposed, cancellationToken))
            {
                using (packetLease)
                {
                    ApplyResyncIfNeeded(codecContext, frame, scheduler, ref sequence, ref lastTimestamp, ref lastResyncVersion, ref prerollDropUntil);
                    while (!_disposed && _context.IsPaused() && !cancellationToken.IsCancellationRequested)
                    {
                        wasPaused = true;
                        ApplyResyncIfNeeded(codecContext, frame, scheduler, ref sequence, ref lastTimestamp, ref lastResyncVersion, ref prerollDropUntil);
                        Thread.Sleep(20);
                    }

                    if (_disposed || cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }

                    if (wasPaused)
                    {
                        scheduler.ResetPresentationClock();
                        wasPaused = false;
                    }

                    // 同步后只丢弃旧位置的包，保留新位置已经读到的关键包。
                    if (packetLease is not null && packetLease.Generation != _input.PacketGeneration())
                    {
                        FluxMediaLog.Write(LogLevel.Information, nameof(FFmpegPacketVideoPlaybackWorker), _context.InstanceId,
                            $"Event=SeekPacketDiscarded Kind=Video PacketGeneration={packetLease.Generation} CurrentGeneration={_input.PacketGeneration()}");
                        continue;
                    }

                    if (packetLease is not null)
                    {
                        if (loggedPacketGeneration != packetLease.Generation)
                        {
                            loggedPacketGeneration = packetLease.Generation;
                            FluxMediaLog.Write(LogLevel.Information, nameof(FFmpegPacketVideoPlaybackWorker), _context.InstanceId, $"Event=SeekFirstVideoPacket Generation={packetLease.Generation} Key={(packetLease.Packet->flags & ffmpeg.AV_PKT_FLAG_KEY) != 0} Pts={packetLease.Packet->pts}");
                        }
                        readPackets++;
                        _callbacks.PacketConsumed?.Invoke((nint)packetLease.Packet);
                    }
                    // 空包通知解码器输出 B 帧等延迟帧。
                    FFmpegErrorHelper.ThrowIfError(ffmpeg.avcodec_send_packet(codecContext, packetLease is null ? null : packetLease.Packet), "avcodec_send_packet");
                    while (!_disposed && !cancellationToken.IsCancellationRequested)
                    {
                        if (ApplyResyncIfNeeded(codecContext, frame, scheduler, ref sequence, ref lastTimestamp, ref lastResyncVersion, ref prerollDropUntil))
                        {
                            break;
                        }

                        var receive = ffmpeg.avcodec_receive_frame(codecContext, frame);
                        if (receive == ffmpeg.AVERROR(ffmpeg.EAGAIN) || receive == ffmpeg.AVERROR_EOF)
                        {
                            break;
                        }

                        FFmpegErrorHelper.ThrowIfError(receive, "avcodec_receive_frame");
                        firstDecodedFrameElapsed ??= DateTimeOffset.UtcNow - workerStartedAt;
                        var timestamp = TimestampFromFrame(frame, _input.Stream.TimeBase, _input.Stream.StartTime, sequence, fallbackFrameInterval, lastTimestamp);
                        lastTimestamp = timestamp;
                        decodedFrames++;
                        if (ShouldDropPrerollFrame(timestamp, ref prerollDropUntil))
                        {
                            droppedVideoFrames++;
                            sequence++;
                            ffmpeg.av_frame_unref(frame);
                            FluxMediaLog.Write(LogLevel.Debug, nameof(FFmpegPacketVideoPlaybackWorker), _context.InstanceId, $"Event=PacketVideoPrerollDrop Timestamp={timestamp} Target={prerollDropUntil?.ToString() ?? "-"}");
                            continue;
                        }

                        VideoFrameSchedulingDecision scheduling = default;
                        while (!_disposed && !cancellationToken.IsCancellationRequested)
                        {
                            if (_context.Resync().Version != lastResyncVersion) break;
                            if (_context.IsPaused())
                            {
                                scheduler.ResetPresentationClock();
                                Thread.Sleep(20);
                                continue;
                            }
                            // 播放时钟：先同步呈现时间，再发布视频帧。
                            scheduling = scheduler.ScheduleVideoFrame(timestamp, cancellationToken);
                            lastVideoBufferedDuration = scheduling.VideoBufferedDuration;
                            lowLatencyCatchUpActive = scheduling.LowLatencyCatchUp;
                            if (scheduling.Action == VideoFrameSchedulingAction.Buffer)
                            {
                                bufferingDecisionCount++;
                            }
                            if (scheduling.Action is not VideoFrameSchedulingAction.Buffer and not VideoFrameSchedulingAction.Wait)
                            {
                                break;
                            }
                        }

                        if (_disposed || cancellationToken.IsCancellationRequested) break;

                        // 等待旧画面时收到定位请求，立即刷新解码器，不能继续提交旧帧。
                        if (ApplyResyncIfNeeded(codecContext, frame, scheduler, ref sequence, ref lastTimestamp, ref lastResyncVersion, ref prerollDropUntil))
                        {
                            break;
                        }

                        lastVideoAudioDistance = scheduling.DistanceToAudio;
                        lastAudioClock = scheduling.AudioClock;
                        if (scheduling.Action == VideoFrameSchedulingAction.Drop)
                        {
                            droppedVideoFrames++;
                            sequence++;
                            ffmpeg.av_frame_unref(frame);
                            if (DateTimeOffset.UtcNow - lastStatsAt >= TimeSpan.FromSeconds(1))
                            {
                                lastStatsAt = DateTimeOffset.UtcNow;
                                ReportDiagnostics(running: true, frameRate: frameRate);
                            }
                            continue;
                        }

                        var publishedSequence = sequence++;
                        VideoFrame? frameToPublish = null;
                        var presentationFrame = frame;
                        var isD3D11Frame = d3d11Hardware is not null && (AVPixelFormat)frame->format == AVPixelFormat.AV_PIX_FMT_D3D11;
                        var isVaapiFrame = vaapiHardware?.IsHardwareFrame(frame) == true;
                        if ((isD3D11Frame && !_callbacks.VideoCapabilities().SupportsD3D11SharedTexture) || isVaapiFrame)
                        {
                            if (softwareFrame == null)
                            {
                                softwareFrame = ffmpeg.av_frame_alloc();
                            }
                            if (softwareFrame == null)
                            {
                                throw new InvalidOperationException("FFmpeg failed to allocate a hardware-frame download target.");
                            }

                            ffmpeg.av_frame_unref(softwareFrame);
                            FFmpegErrorHelper.ThrowIfError(ffmpeg.av_hwframe_transfer_data(softwareFrame, frame, 0), "av_hwframe_transfer_data");
                            presentationFrame = softwareFrame;
                            _videoPresentationBackend = isVaapiFrame ? "CPU BGRA (VAAPI download)" : "CPU BGRA (D3D11VA download)";
                            if (!isVaapiFrame)
                            {
                                _hardwareAccelerationFailureReason ??= "GPU presentation was disabled after the decoder started.";
                            }
                        }

                        if (d3d11Hardware is not null && presentationFrame == frame && (AVPixelFormat)frame->format == AVPixelFormat.AV_PIX_FMT_D3D11)
                        {
                            try
                            {
                                frameToPublish = d3d11Hardware.ConvertFrame(frame, new VideoFormat(frame->width, frame->height, frameRate <= 0 ? 25 : frameRate, CorePixelFormat.Nv12), timestamp, publishedSequence);
                            }
                            catch (Exception ex) when (_options.VideoAcceleration == VideoAccelerationMode.Auto)
                            {
                                _videoPresentationBackend = "CPU BGRA (D3D11VA download)";
                                _hardwareAccelerationFailureReason = $"D3D11 video processor presentation failed: {ex.Message}";
                                FluxMediaLog.Write(
                                    LogLevel.Warning, nameof(FFmpegPacketVideoPlaybackWorker), _context.InstanceId, $"Event=HardwarePresentationFallback Backend=D3D11VA Error={ex.Message}", ex);

                                if (softwareFrame == null)
                                {
                                    softwareFrame = ffmpeg.av_frame_alloc();
                                }
                                if (softwareFrame == null)
                                {
                                    throw new InvalidOperationException(
                                        "FFmpeg failed to allocate a hardware-frame download target.");
                                }

                                ffmpeg.av_frame_unref(softwareFrame);
                                FFmpegErrorHelper.ThrowIfError(
                                    ffmpeg.av_hwframe_transfer_data(softwareFrame, frame, 0), "av_hwframe_transfer_data");
                                presentationFrame = softwareFrame;
                            }
                        }

                        if (frameToPublish is null)
                        {
                            EnsureScaleContext(presentationFrame, ref width, ref height, ref sourcePixelFormat, ref swsContext);
                            var destinationStride = checked(width * 4);
                            var frameLength = checked(height * destinationStride);
                            var frameOwner = VideoFramePool.Rent(frameLength);
                            try
                            {
                                fixed (byte* target = frameOwner.Memory.Span)
                                {
                                    byte_ptrArray4 destinationData = default;
                                    int_array4 destinationLinesize = default;
                                    destinationData[0] = target;
                                    destinationLinesize[0] = destinationStride;
                                    var scaledRows = ffmpeg.sws_scale(swsContext, presentationFrame->data, presentationFrame->linesize, 0, height, destinationData, destinationLinesize);
                                    if (scaledRows != height)
                                    {
                                        throw new InvalidOperationException($"Video pixel conversion returned {scaledRows} rows; expected {height}.");
                                    }
                                }
                            }
                            catch
                            {
                                frameOwner.Release();
                                throw;
                            }

                            frameToPublish = new VideoFrame(new VideoFormat(width, height, frameRate <= 0 ? 25 : frameRate, CorePixelFormat.Bgra32), frameOwner, frameLength, timestamp, publishedSequence, destinationStride);
                        }
                        lastFrameAt = DateTimeOffset.UtcNow;

                        if (!firstFrameLogged)
                        {
                            firstFrameLogged = true;
                            FluxMediaLog.Write(LogLevel.Information, nameof(FFmpegPacketVideoPlaybackWorker), _context.InstanceId, $"Event=PacketVideoFirstFrame SourceKind={_context.Source.Kind} Url={_context.Source.Uri} FirstDecodedFrameElapsed={firstDecodedFrameElapsed.Value.TotalMilliseconds:0}ms Timestamp={timestamp}");
                        }

                        try
                        {
                            frameToPublish!.BindEpoch(_frameEpoch, _producerEpoch);
                            // 视频链路①触发：把当前帧交给帧泵。
                            _callbacks.FrameReady(frameToPublish);
                            frameToPublish = null;
                        }
                        finally
                        {
                            frameToPublish?.Dispose();
                        }

                        pushedFrames++;
                        if (softwareFrame != null)
                        {
                            ffmpeg.av_frame_unref(softwareFrame);
                        }

                        if (DateTimeOffset.UtcNow - lastStatsAt >= TimeSpan.FromSeconds(1) || publishedSequence == 0)
                        {
                            lastStatsAt = DateTimeOffset.UtcNow;
                            ReportDiagnostics(running: true, frameRate: frameRate);
                        }
                    }
                }
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
            FluxMediaLog.Write(LogLevel.Error, nameof(FFmpegPacketVideoPlaybackWorker), _context.InstanceId, $"Event=PacketVideoWorkerFault SourceKind={_context.Source.Kind} Url={_context.Source.Uri} Error={ex.Message}", ex);
            ReportDiagnostics(running: false);
            throw;
        }
        finally
        {
            var cleanupErrors = new List<Exception>();
            if (swsContext != null)
                ffmpeg.sws_freeContext(swsContext);
            Release(() => d3d11Hardware?.Dispose());
            Release(() => vaapiHardware?.Dispose());
            if (softwareFrame != null)
                ffmpeg.av_frame_free(&softwareFrame);
            if (frame != null)
                ffmpeg.av_frame_free(&frame);
            if (codecContext != null)
                ffmpeg.avcodec_free_context(&codecContext);
            var cleanupFailure = cleanupErrors.Count > 0 ? new AggregateException("视频解码资源释放失败。", cleanupErrors) : null;
            var throwCleanupFailure = lastError is null && cleanupFailure is not null;
            if (throwCleanupFailure)
            {
                lastError = cleanupFailure!.Message;
                exitReason = lastError;
            }
            FluxMediaLog.Write(LogLevel.Information, nameof(FFmpegPacketVideoPlaybackWorker), _context.InstanceId, $"Event=PacketVideoWorkerExit SourceKind={_context.Source.Kind} Url={_context.Source.Uri} Reason={exitReason} ReadPackets={readPackets} DecodedFrames={decodedFrames} PushedFrames={pushedFrames}");
            ReportDiagnostics(running: false);
            if (throwCleanupFailure) throw cleanupFailure!;

            // 硬件上下文释放失败也不跳过其它解码资源。
            void Release(Action release)
            {
                try { release(); }
                catch (Exception exception)
                {
                    cleanupErrors.Add(exception);
                    FluxMediaLog.Write(LogLevel.Error, nameof(FFmpegPacketVideoPlaybackWorker), _context.InstanceId, "Event=VideoCleanupFailed", exception);
                }
            }
        }

        // 从本次运行统计生成诊断快照。
        void ReportDiagnostics(bool running, double? frameRate = null)
        {
            _callbacks.Diagnostics.Invoke(new PlaybackWorkerDiagnostics
            {
                SourceKind = _context.Source.Kind,
                SourceUrl = _context.Source.Uri,
                FirstDecodedFrameElapsed = firstDecodedFrameElapsed,
                Protocol = MediaSourcePolicy.GetProtocol(_context.Source),
                FrameRate = frameRate,
                TimingMode = timingMode,
                LowLatencyEnabled = timingMode == PlaybackTimingMode.RealtimeLowLatency,
                ReadPacketCount = readPackets,
                DecodedFrameCount = decodedFrames,
                PushedFrameCount = pushedFrames,
                DroppedVideoFrameCount = droppedVideoFrames,
                BufferingDecisionCount = bufferingDecisionCount,
                LastVideoBufferedDuration = lastVideoBufferedDuration,
                LowLatencyCatchUpActive = lowLatencyCatchUpActive,
                LastVideoAudioDistance = lastVideoAudioDistance,
                LastAudioClock = lastAudioClock,
                LastFrameAt = lastFrameAt == DateTimeOffset.MinValue ? null : lastFrameAt,
                WorkerRunning = running,
                ExitReason = running ? null : exitReason,
                LastFFmpegError = lastError,
                HardwareAccelerationRequested = _hardwareAccelerationRequested,
                HardwareAccelerationActive = _hardwareAccelerationActive,
                VideoDecoderBackend = _videoDecoderBackend,
                VideoPresentationBackend = _videoPresentationBackend,
                HardwareAccelerationFailureReason = _hardwareAccelerationFailureReason
            });
        }
    }

    /// <summary>
    /// 发现新的同步请求时重置解码状态。
    /// </summary>
    private bool ApplyResyncIfNeeded(AVCodecContext* codecContext, AVFrame* frame, PlaybackScheduler scheduler, ref long sequence, ref TimeSpan lastTimestamp, ref long lastResyncVersion, ref TimeSpan? prerollDropUntil)
    {
        var request = _context.Resync();
        if (request.Version == lastResyncVersion)
        {
            return false;
        }

        lastResyncVersion = request.Version;
        _frameEpoch = request.FrameEpoch;
        ffmpeg.avcodec_flush_buffers(codecContext);
        if (frame != null)
        {
            ffmpeg.av_frame_unref(frame);
        }

        scheduler.Reset();
        FluxMediaLog.Write(LogLevel.Information, nameof(FFmpegPacketVideoPlaybackWorker), _context.InstanceId,
            $"Event=SeekFrameScheduling Version={request.Version} Policy=ImmediateFirstFrame");
        sequence = 0;
        lastTimestamp = request.Position;
        prerollDropUntil = request.Position;
        FluxMediaLog.Write(LogLevel.Information, nameof(FFmpegPacketVideoPlaybackWorker), _context.InstanceId, $"Event=PacketVideoDecoderResync Version={request.Version} Position={request.Position}");
        return true;
    }

    /// <summary>
    /// 判断是否应丢弃目标时间之前的预滚帧。
    /// </summary>
    private static bool ShouldDropPrerollFrame(TimeSpan timestamp, ref TimeSpan? prerollDropUntil)
    {
        var target = prerollDropUntil;
        if (target is null)
        {
            return false;
        }

        if (timestamp + SeekPrerollTolerance < target.Value)
        {
            return true;
        }

        prerollDropUntil = null;
        return false;
    }

    /// <summary>
    /// 计算视频帧时间，异常时使用备用时间。
    /// </summary>
    private static TimeSpan TimestampFromFrame(AVFrame* frame, AVRational timeBase, TimeSpan timestampOrigin, long sequence, TimeSpan fallbackFrameInterval, TimeSpan lastTimestamp)
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

        return sequence == 0 ? TimeSpan.Zero : lastTimestamp + fallbackFrameInterval;
    }

    /// <summary>
    /// 按当前像素格式创建或更新缩放上下文。
    /// </summary>
    private static void EnsureScaleContext(AVFrame* frame, ref int width, ref int height, ref AVPixelFormat sourcePixelFormat, ref SwsContext* swsContext)
    {
        if (frame->format < 0)
        {
            throw new InvalidOperationException("Decoded video frame has an invalid pixel format.");
        }

        var frameWidth = frame->width > 0 ? frame->width : width;
        var frameHeight = frame->height > 0 ? frame->height : height;
        var framePixelFormat = (AVPixelFormat)frame->format;
        if (swsContext != null && frameWidth == width && frameHeight == height && framePixelFormat == sourcePixelFormat)
        {
            return;
        }

        if (swsContext != null)
        {
            ffmpeg.sws_freeContext(swsContext);
            swsContext = null;
        }

        width = frameWidth;
        height = frameHeight;
        sourcePixelFormat = framePixelFormat;
        swsContext = ffmpeg.sws_getContext(width, height, sourcePixelFormat, width, height, AVPixelFormat.AV_PIX_FMT_BGRA, (int)SwsFlags.SWS_BILINEAR, null, null, null);
        if (swsContext == null)
        {
            throw new InvalidOperationException("Video pixel format conversion context creation failed.");
        }
    }

    /// <summary>
    /// 将原生错误码转换为可读信息。
    /// </summary>
    private static string? FormatError(int code) => code < 0 ? FFmpegErrorHelper.GetErrorMessage(code) : null;
}
