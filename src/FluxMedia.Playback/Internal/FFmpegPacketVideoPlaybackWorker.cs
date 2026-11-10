using System.Diagnostics;
using System.Threading.Channels;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen;
using FluxMedia.Core.Configuration;
using FluxMedia.Core.Logging;
using FluxMedia.Core.Models;
using FluxMedia.Playback.Internal;
using FluxMedia.FFmpeg.Native;
using FluxMedia.FFmpeg.Pipeline;
using FluxMedia.Playback.Internal.Hardware;
using CorePixelFormat = FluxMedia.Core.Models.PixelFormat;

namespace FluxMedia.Playback.Internal;

/// <summary>
/// 消费视频包并解码、调度和输出视频帧。
/// </summary>
internal sealed unsafe class FFmpegPacketVideoPlaybackWorker : IDisposable
{
    /// <summary>
    /// 跳转预滚容差。
    /// </summary>
    private static readonly TimeSpan SeekPrerollTolerance = TimeSpan.FromMilliseconds(15);

    /// <summary>
    /// 源。
    /// </summary>
    private readonly MediaSource _source;

    /// <summary>
    /// 配置。
    /// </summary>
    private readonly PlaybackOptions _options;

    /// <summary>
    /// 实例标识。
    /// </summary>
    private readonly Guid _instanceId;

    /// <summary>
    /// 流。
    /// </summary>
    private readonly FFmpegStreamDescriptor _stream;

    /// <summary>
    /// 供解码线程消费的数据包读取器。
    /// </summary>
    private readonly ChannelReader<FFmpegPacketLease> _packets;

    /// <summary>
    /// 当前读取位置的数据包代次。
    /// </summary>
    private readonly Func<long> _packetGeneration;

    /// <summary>
    /// 接收解码视频帧的回调。
    /// </summary>
    private readonly Action<VideoFrame> _onFrame;

    /// <summary>
    /// 读取当前暂停状态的回调。
    /// </summary>
    private readonly Func<bool> _isPaused;

    /// <summary>
    /// 读取当前播放速度的回调。
    /// </summary>
    private readonly Func<double> _playbackRate;

    /// <summary>
    /// 读取最新音频诊断的回调。
    /// </summary>
    private readonly Func<AudioPlaybackDiagnostics?> _audioDiagnostics;

    /// <summary>
    /// 读取视频缓冲时长的回调。
    /// </summary>
    private readonly Func<TimeSpan> _videoBufferedDuration;

    /// <summary>
    /// 读取视频缓冲包数的回调。
    /// </summary>
    private readonly Func<int> _videoBufferedPacketCount;

    /// <summary>
    /// 读取最新解码同步请求的回调。
    /// </summary>
    private readonly Func<PlaybackResyncRequest> _resyncRequest;

    /// <summary>
    /// 显示端最新声明的 GPU 输出能力。
    /// </summary>
    private readonly Func<VideoOutputCapabilities> _videoOutputCapabilities;

    /// <summary>
    /// 接收已消费数据包的录制回调。
    /// </summary>
    private readonly FFmpegPacketConsumer? _recordingPacketConsumer;

    /// <summary>
    /// 接收工作线程诊断信息的回调。
    /// </summary>
    private readonly Action<PlaybackWorkerDiagnostics>? _onDiagnostics;

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
    /// 新管线目标位置，之前的帧仅用于预滚解码。
    /// </summary>
    private readonly TimeSpan _startPosition;

    /// <summary>
    /// 初始化 FFmpeg 数据包视频播放工作线程所需的状态和资源。
    /// </summary>
    public FFmpegPacketVideoPlaybackWorker(
        MediaSource source, PlaybackOptions options, Guid instanceId, TimeSpan startPosition, FFmpegStreamDescriptor stream, ChannelReader<FFmpegPacketLease> packets, Func<long> packetGeneration, Action<VideoFrame> onFrame, Func<bool> isPaused, Func<double> playbackRate, Action<PlaybackWorkerDiagnostics>? onDiagnostics = null, Func<AudioPlaybackDiagnostics?>? audioDiagnostics = null, Func<TimeSpan>? videoBufferedDuration = null, Func<int>? videoBufferedPacketCount = null, Func<PlaybackResyncRequest>? resyncRequest = null, Func<VideoOutputCapabilities>? videoOutputCapabilities = null, FFmpegPacketConsumer? recordingPacketConsumer = null)
    {
        _source = source;
        _options = options;
        _instanceId = instanceId;
        _startPosition = startPosition;
        _stream = stream;
        _packets = packets;
        _packetGeneration = packetGeneration;
        _onFrame = onFrame;
        _isPaused = isPaused;
        _playbackRate = playbackRate;
        _audioDiagnostics = audioDiagnostics ?? (() => null);
        _videoBufferedDuration = videoBufferedDuration ?? (() => TimeSpan.Zero);
        _videoBufferedPacketCount = videoBufferedPacketCount ?? (() => 0);
        _resyncRequest = resyncRequest ?? (() => default);
        _videoOutputCapabilities = videoOutputCapabilities ?? (() => new VideoOutputCapabilities());
        _recordingPacketConsumer = recordingPacketConsumer;
        _onDiagnostics = onDiagnostics;
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
        FluxMediaLog.Write(LogLevel.Information, nameof(FFmpegPacketVideoPlaybackWorker), _instanceId, $"Event=PacketVideoWorkerDispose SourceKind={_source.Kind} Url={_source.Uri}");
    }

    /// <summary>
    /// 读取数据包并循环解码。
    /// </summary>
    private void DecodeLoop(CancellationToken cancellationToken)
    {
        var workerStartedAt = DateTimeOffset.UtcNow;
        var timingMode = ResolveTimingMode();
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

        AVCodecContext* codecContext = null;
        AVFrame* frame = null;
        AVFrame* softwareFrame = null;
        SwsContext* swsContext = null;
        D3D11HardwareVideoContext? d3d11Hardware = null;
        VaapiHardwareVideoContext? vaapiHardware = null;

        try
        {
            var codec = ffmpeg.avcodec_find_decoder(_stream.CodecParameters->codec_id);
            if (codec == null)
            {
                throw new InvalidOperationException("No FFmpeg video decoder is available for the selected stream.");
            }

            codecContext = ffmpeg.avcodec_alloc_context3(codec);
            if (codecContext == null)
            {
                throw new InvalidOperationException("FFmpeg video codec context allocation failed.");
            }

            FFmpegErrorHelper.ThrowIfError(ffmpeg.avcodec_parameters_to_context(codecContext, _stream.CodecParameters), "avcodec_parameters_to_context");
            var capabilities = _videoOutputCapabilities();
            _hardwareAccelerationRequested = _options.VideoAcceleration != VideoAccelerationMode.Disabled;
            string? d3d11Error = null;
            string? vaapiError = null;
            var hardwareInitStartedAt = Stopwatch.GetTimestamp();
            if (_options.VideoAcceleration != VideoAccelerationMode.Disabled &&
                capabilities.SupportsD3D11SharedTexture &&
                D3D11HardwareVideoContext.TryCreate(
                    codec, capabilities.AdapterLuid, _instanceId, out d3d11Hardware, out d3d11Error))
            {
                d3d11Hardware!.ConfigureCodec(codecContext);
                _hardwareAccelerationActive = true;
                _videoDecoderBackend = "D3D11VA";
                _videoPresentationBackend = "D3D11 shared BGRA texture";
            }
            else if (_options.VideoAcceleration != VideoAccelerationMode.Disabled &&
                RuntimeInformation.IsOSPlatform(OSPlatform.Linux) &&
                VaapiHardwareVideoContext.TryCreate(
                    codec, _instanceId, out vaapiHardware, out vaapiError))
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
                throw new InvalidOperationException(
                    d3d11Error ?? vaapiError ?? "No compatible hardware video output was configured.");
            }
            else if (_hardwareAccelerationRequested)
            {
                _hardwareAccelerationFailureReason = d3d11Error ??
                    vaapiError ??
                    "The active video output cannot import D3D11 shared textures.";
            }

            var hardwareInitElapsed = PlaybackClock.ElapsedSince(hardwareInitStartedAt);
            FluxMediaLog.Write(
                LogLevel.Information, nameof(FFmpegPacketVideoPlaybackWorker), _instanceId, $"Event=PacketVideoHardwareInitCompleted SourceKind={_source.Kind} Url={_source.Uri} Requested={_hardwareAccelerationRequested} Active={_hardwareAccelerationActive} DecoderBackend={_videoDecoderBackend} PresentationBackend={_videoPresentationBackend} Failure={_hardwareAccelerationFailureReason ?? "-"} ElapsedMs={hardwareInitElapsed.TotalMilliseconds:0}");

            FluxMediaLog.Write(
                LogLevel.Information, nameof(FFmpegPacketVideoPlaybackWorker), _instanceId, $"Event=PacketVideoCodecOpenStart SourceKind={_source.Kind} Url={_source.Uri} Decoder={ffmpeg.avcodec_get_name(_stream.CodecParameters->codec_id)} Backend={_videoDecoderBackend}");
            var codecOpenStartedAt = Stopwatch.GetTimestamp();
            var codecOpenResult = ffmpeg.avcodec_open2(codecContext, codec, null);
            var codecOpenElapsed = PlaybackClock.ElapsedSince(codecOpenStartedAt);
            FluxMediaLog.Write(
                codecOpenResult < 0 ? LogLevel.Warning : LogLevel.Information, nameof(FFmpegPacketVideoPlaybackWorker), _instanceId, $"Event=PacketVideoCodecOpenCompleted SourceKind={_source.Kind} Url={_source.Uri} Backend={_videoDecoderBackend} Result={codecOpenResult} Error={FormatError(codecOpenResult) ?? "-"} ElapsedMs={codecOpenElapsed.TotalMilliseconds:0}");
            if (codecOpenResult < 0 &&
                (d3d11Hardware is not null || vaapiHardware is not null) &&
                _options.VideoAcceleration == VideoAccelerationMode.Auto)
            {
                var failedBackend = d3d11Hardware is not null ? "D3D11VA" : "VAAPI";
                FluxMediaLog.Write(
                    LogLevel.Warning, nameof(FFmpegPacketVideoPlaybackWorker), _instanceId, $"Event=HardwareDecoderOpenFallback Backend={failedBackend} Error={FFmpegErrorHelper.GetErrorMessage(codecOpenResult)}");
                d3d11Hardware?.Dispose();
                d3d11Hardware = null;
                vaapiHardware?.Dispose();
                vaapiHardware = null;
                _hardwareAccelerationActive = false;
                _videoDecoderBackend = "Software";
                _videoPresentationBackend = "CPU BGRA";
                _hardwareAccelerationFailureReason =
                    $"{failedBackend} decoder open failed: {FFmpegErrorHelper.GetErrorMessage(codecOpenResult)}";
                ffmpeg.avcodec_free_context(&codecContext);
                codecContext = ffmpeg.avcodec_alloc_context3(codec);
                if (codecContext == null)
                {
                    throw new InvalidOperationException("FFmpeg video codec context allocation failed during hardware fallback.");
                }

                FFmpegErrorHelper.ThrowIfError(
                    ffmpeg.avcodec_parameters_to_context(codecContext, _stream.CodecParameters), "avcodec_parameters_to_context");
                FluxMediaLog.Write(
                    LogLevel.Information, nameof(FFmpegPacketVideoPlaybackWorker), _instanceId, $"Event=PacketVideoCodecOpenStart SourceKind={_source.Kind} Url={_source.Uri} Decoder={ffmpeg.avcodec_get_name(_stream.CodecParameters->codec_id)} Backend={_videoDecoderBackend}");
                codecOpenStartedAt = Stopwatch.GetTimestamp();
                codecOpenResult = ffmpeg.avcodec_open2(codecContext, codec, null);
                codecOpenElapsed = PlaybackClock.ElapsedSince(codecOpenStartedAt);
                FluxMediaLog.Write(
                    codecOpenResult < 0 ? LogLevel.Warning : LogLevel.Information, nameof(FFmpegPacketVideoPlaybackWorker), _instanceId, $"Event=PacketVideoCodecOpenCompleted SourceKind={_source.Kind} Url={_source.Uri} Backend={_videoDecoderBackend} Result={codecOpenResult} Error={FormatError(codecOpenResult) ?? "-"} ElapsedMs={codecOpenElapsed.TotalMilliseconds:0}");
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
            var frameRate = FrameRateCalculator.FromRationalPair(
                _stream.AverageFrameRate.num, _stream.AverageFrameRate.den, _stream.RealFrameRate.num, _stream.RealFrameRate.den);
            var fallbackFrameInterval = TimeSpan.FromSeconds(1d / Math.Max(frameRate, 25d));
            var initialResync = _resyncRequest();
            var lastResyncVersion = initialResync.Version;
            _frameEpoch = initialResync.FrameEpoch;
            var initialPosition = initialResync.Version > 0 ? initialResync.Position : _startPosition;
            TimeSpan? prerollDropUntil = initialPosition > TimeSpan.Zero ? initialPosition : null;
            var scheduler = new PlaybackScheduler(
                timingMode, _playbackRate, () => _disposed || _resyncRequest().Version != lastResyncVersion, _audioDiagnostics, _videoBufferedDuration, _videoBufferedPacketCount, minBufferDuration: _source.Kind == MediaSourceKind.File ? TimeSpan.Zero : TimeSpan.FromMilliseconds(500), maxLowLatencyDelay: _options.Network.MaxNetworkFrameDelay);
            var lastTimestamp = TimeSpan.Zero;
            var lastStatsAt = DateTimeOffset.UtcNow;
            if (initialPosition > TimeSpan.Zero) scheduler.Reset();
            var loggedPacketGeneration = -1L;
            var wasPaused = false;

            ReportDiagnostics(null, null, firstDecodedFrameElapsed, readPackets, decodedFrames, pushedFrames, droppedVideoFrames, bufferingDecisionCount, lastVideoBufferedDuration, lowLatencyCatchUpActive, lastVideoAudioDistance, lastAudioClock, lastFrameAt, true, null, null, null, timingMode, frameRate);
            FluxMediaLog.Write(LogLevel.Information, nameof(FFmpegPacketVideoPlaybackWorker), _instanceId, $"Event=PacketVideoWorkerStart SourceKind={_source.Kind} Url={_source.Uri} StreamIndex={_stream.StreamIndex} Decoder={ffmpeg.avcodec_get_name(_stream.CodecParameters->codec_id)} FrameRate={frameRate:0.###}");

            foreach (var packetLease in FFmpegPacketReader.ReadWithDrain(_packets, () => _disposed, cancellationToken))
            {
                using (packetLease)
                {
                    ApplyResyncIfNeeded(
                        codecContext, frame, scheduler, ref sequence, ref lastTimestamp, ref lastResyncVersion, ref prerollDropUntil);
                    while (!_disposed && _isPaused() && !cancellationToken.IsCancellationRequested)
                    {
                        wasPaused = true;
                        ApplyResyncIfNeeded(
                            codecContext, frame, scheduler, ref sequence, ref lastTimestamp, ref lastResyncVersion, ref prerollDropUntil);
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
                    if (packetLease is not null && packetLease.Generation != _packetGeneration())
                    {
                        FluxMediaLog.Write(LogLevel.Information, nameof(FFmpegPacketVideoPlaybackWorker), _instanceId,
                            $"Event=SeekPacketDiscarded Kind=Video PacketGeneration={packetLease.Generation} CurrentGeneration={_packetGeneration()}");
                        continue;
                    }

                    if (packetLease is not null)
                    {
                        if (loggedPacketGeneration != packetLease.Generation)
                        {
                            loggedPacketGeneration = packetLease.Generation;
                            FluxMediaLog.Write(LogLevel.Information, nameof(FFmpegPacketVideoPlaybackWorker), _instanceId,
                                $"Event=SeekFirstVideoPacket Generation={packetLease.Generation} Key={(packetLease.Packet->flags & ffmpeg.AV_PKT_FLAG_KEY) != 0} Pts={packetLease.Packet->pts}");
                        }
                        readPackets++;
                        _recordingPacketConsumer?.Invoke((nint)packetLease.Packet);
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
                        var timestamp = TimestampFromFrame(
                            frame, _stream.TimeBase, _stream.StartTime, sequence, fallbackFrameInterval, lastTimestamp);
                        lastTimestamp = timestamp;
                        decodedFrames++;
                        if (ShouldDropPrerollFrame(timestamp, ref prerollDropUntil))
                        {
                            droppedVideoFrames++;
                            sequence++;
                            ffmpeg.av_frame_unref(frame);
                            FluxMediaLog.Write(LogLevel.Debug, nameof(FFmpegPacketVideoPlaybackWorker), _instanceId, $"Event=PacketVideoPrerollDrop Timestamp={timestamp} Target={prerollDropUntil?.ToString() ?? "-"}");
                            continue;
                        }

                        VideoFrameSchedulingDecision scheduling = default;
                        do
                        {
                            if (_resyncRequest().Version != lastResyncVersion) break;
                            scheduling = scheduler.ScheduleVideoFrame(timestamp, fallbackFrameInterval, cancellationToken);
                            lastVideoBufferedDuration = scheduling.VideoBufferedDuration;
                            lowLatencyCatchUpActive = scheduling.LowLatencyCatchUp;
                            if (scheduling.Action == VideoFrameSchedulingAction.Buffer)
                            {
                                bufferingDecisionCount++;
                            }
                        } while (!_disposed &&
                            !cancellationToken.IsCancellationRequested &&
                            scheduling.Action == VideoFrameSchedulingAction.Buffer);

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
                                ReportDiagnostics(null, null, firstDecodedFrameElapsed, readPackets, decodedFrames, pushedFrames, droppedVideoFrames, bufferingDecisionCount, lastVideoBufferedDuration, lowLatencyCatchUpActive, lastVideoAudioDistance, lastAudioClock, lastFrameAt, true, null, null, null, timingMode, frameRate);
                            }

                            continue;
                        }

                        var publishedSequence = sequence++;
                        VideoFrame? frameToPublish = null;
                        var presentationFrame = frame;
                        var isD3D11Frame = d3d11Hardware is not null &&
                            (AVPixelFormat)frame->format == AVPixelFormat.AV_PIX_FMT_D3D11;
                        var isVaapiFrame = vaapiHardware?.IsHardwareFrame(frame) == true;
                        if ((isD3D11Frame &&
                            !_videoOutputCapabilities().SupportsD3D11SharedTexture) ||
                            isVaapiFrame)
                        {
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
                            _videoPresentationBackend = isVaapiFrame
                                ? "CPU BGRA (VAAPI download)"
                                : "CPU BGRA (D3D11VA download)";
                            if (!isVaapiFrame)
                            {
                                _hardwareAccelerationFailureReason ??=
                                    "GPU presentation was disabled after the decoder started.";
                            }
                        }

                        if (d3d11Hardware is not null &&
                            presentationFrame == frame &&
                            (AVPixelFormat)frame->format == AVPixelFormat.AV_PIX_FMT_D3D11)
                        {
                            try
                            {
                                frameToPublish = d3d11Hardware.ConvertFrame(
                                    frame, new VideoFormat(
                                        frame->width, frame->height, frameRate <= 0 ? 25 : frameRate, CorePixelFormat.Nv12), timestamp, publishedSequence);
                            }
                            catch (Exception ex) when (
                                _options.VideoAcceleration == VideoAccelerationMode.Auto)
                            {
                                _videoPresentationBackend = "CPU BGRA (D3D11VA download)";
                                _hardwareAccelerationFailureReason =
                                    $"D3D11 video processor presentation failed: {ex.Message}";
                                FluxMediaLog.Write(
                                    LogLevel.Warning, nameof(FFmpegPacketVideoPlaybackWorker), _instanceId, $"Event=HardwarePresentationFallback Backend=D3D11VA Error={ex.Message}", ex);

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
                                    var scaledRows = ffmpeg.sws_scale(
                                        swsContext, presentationFrame->data, presentationFrame->linesize, 0, height, destinationData, destinationLinesize);
                                    if (scaledRows != height)
                                    {
                                        throw new InvalidOperationException(
                                            $"Video pixel conversion returned {scaledRows} rows; expected {height}.");
                                    }
                                }
                            }
                            catch
                            {
                                frameOwner.Release();
                                throw;
                            }

                            frameToPublish = new VideoFrame(
                                new VideoFormat(width, height, frameRate <= 0 ? 25 : frameRate, CorePixelFormat.Bgra32), frameOwner, frameLength, timestamp, publishedSequence, destinationStride);
                        }
                        lastFrameAt = DateTimeOffset.UtcNow;

                        if (!firstFrameLogged)
                        {
                            firstFrameLogged = true;
                            FluxMediaLog.Write(LogLevel.Information, nameof(FFmpegPacketVideoPlaybackWorker), _instanceId, $"Event=PacketVideoFirstFrame SourceKind={_source.Kind} Url={_source.Uri} FirstDecodedFrameElapsed={firstDecodedFrameElapsed.Value.TotalMilliseconds:0}ms Timestamp={timestamp}");
                        }

                        try
                        {
                            frameToPublish!.BindEpoch(_frameEpoch, _producerEpoch);
                            _onFrame(frameToPublish);
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
                            ReportDiagnostics(null, null, firstDecodedFrameElapsed, readPackets, decodedFrames, pushedFrames, droppedVideoFrames, bufferingDecisionCount, lastVideoBufferedDuration, lowLatencyCatchUpActive, lastVideoAudioDistance, lastAudioClock, lastFrameAt, true, null, null, null, timingMode, frameRate);
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
            exitReason = ex.Message;
            FluxMediaLog.Write(LogLevel.Error, nameof(FFmpegPacketVideoPlaybackWorker), _instanceId, $"Event=PacketVideoWorkerFault SourceKind={_source.Kind} Url={_source.Uri} Error={ex.Message}", ex);
            ReportDiagnostics(null, null, firstDecodedFrameElapsed, readPackets, decodedFrames, pushedFrames, droppedVideoFrames, bufferingDecisionCount, lastVideoBufferedDuration, lowLatencyCatchUpActive, lastVideoAudioDistance, lastAudioClock, lastFrameAt, false, exitReason, null, ex.Message, timingMode, null);
            throw;
        }
        finally
        {
            if (swsContext != null)
                ffmpeg.sws_freeContext(swsContext);
            d3d11Hardware?.Dispose();
            vaapiHardware?.Dispose();
            if (softwareFrame != null)
                ffmpeg.av_frame_free(&softwareFrame);
            if (frame != null)
                ffmpeg.av_frame_free(&frame);
            if (codecContext != null)
                ffmpeg.avcodec_free_context(&codecContext);
            FluxMediaLog.Write(LogLevel.Information, nameof(FFmpegPacketVideoPlaybackWorker), _instanceId, $"Event=PacketVideoWorkerExit SourceKind={_source.Kind} Url={_source.Uri} Reason={exitReason} ReadPackets={readPackets} DecodedFrames={decodedFrames} PushedFrames={pushedFrames}");
            ReportDiagnostics(null, null, firstDecodedFrameElapsed, readPackets, decodedFrames, pushedFrames, droppedVideoFrames, bufferingDecisionCount, lastVideoBufferedDuration, lowLatencyCatchUpActive, lastVideoAudioDistance, lastAudioClock, lastFrameAt, false, exitReason, null, null, timingMode, null);
        }
    }

    /// <summary>
    /// 发现新的同步请求时重置解码状态。
    /// </summary>
    private bool ApplyResyncIfNeeded(
        AVCodecContext* codecContext, AVFrame* frame, PlaybackScheduler scheduler, ref long sequence, ref TimeSpan lastTimestamp, ref long lastResyncVersion, ref TimeSpan? prerollDropUntil)
    {
        var request = _resyncRequest();
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
        FluxMediaLog.Write(LogLevel.Information, nameof(FFmpegPacketVideoPlaybackWorker), _instanceId,
            $"Event=SeekFrameScheduling Version={request.Version} Policy=ImmediateFirstFrame");
        sequence = 0;
        lastTimestamp = request.Position;
        prerollDropUntil = request.Position;
        FluxMediaLog.Write(LogLevel.Information, nameof(FFmpegPacketVideoPlaybackWorker), _instanceId, $"Event=PacketVideoDecoderResync Version={request.Version} Position={request.Position}");
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
    private static TimeSpan TimestampFromFrame(
        AVFrame* frame, AVRational timeBase, TimeSpan timestampOrigin, long sequence, TimeSpan fallbackFrameInterval, TimeSpan lastTimestamp)
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
    private static void EnsureScaleContext(
        AVFrame* frame, ref int width, ref int height, ref AVPixelFormat sourcePixelFormat, ref SwsContext* swsContext)
    {
        if (frame->format < 0)
        {
            throw new InvalidOperationException("Decoded video frame has an invalid pixel format.");
        }

        var frameWidth = frame->width > 0 ? frame->width : width;
        var frameHeight = frame->height > 0 ? frame->height : height;
        var framePixelFormat = (AVPixelFormat)frame->format;
        if (swsContext != null &&
            frameWidth == width &&
            frameHeight == height &&
            framePixelFormat == sourcePixelFormat)
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
    /// 根据媒体源和配置选择帧调度模式。
    /// </summary>
    private PlaybackTimingMode ResolveTimingMode()
    {
        if (_options.Network.TimingMode is PlaybackTimingMode.PresentationTimestamp or PlaybackTimingMode.RealtimeLowLatency)
        {
            return _options.Network.TimingMode;
        }

        return _source.Kind == MediaSourceKind.NetworkStream && _options.Network.LowLatencyMode
            ? PlaybackTimingMode.RealtimeLowLatency
            : PlaybackTimingMode.PresentationTimestamp;
    }

    /// <summary>
    /// 上报视频解码和调度统计。
    /// </summary>
    private void ReportDiagnostics(TimeSpan? openElapsed, TimeSpan? streamInfoElapsed, TimeSpan? firstDecodedFrameElapsed, long readPackets, long decodedFrames, long pushedFrames, long droppedVideoFrames, long bufferingDecisionCount, TimeSpan? videoBufferedDuration, bool lowLatencyCatchUpActive, TimeSpan? videoAudioDistance, TimeSpan? audioClock, DateTimeOffset lastFrameAt, bool running, string? exitReason, int? errorCode, string? error, PlaybackTimingMode timingMode, double? frameRate)
    {
        _onDiagnostics?.Invoke(new PlaybackWorkerDiagnostics
        {
            SourceKind = _source.Kind,
            SourceUrl = _source.Uri,
            OpenElapsed = openElapsed,
            StreamInfoElapsed = streamInfoElapsed,
            FirstDecodedFrameElapsed = firstDecodedFrameElapsed,
            Protocol = ProtocolOf(_source),
            FrameRate = frameRate,
            TimingMode = timingMode,
            LowLatencyEnabled = timingMode == PlaybackTimingMode.RealtimeLowLatency,
            ReadPacketCount = readPackets,
            DecodedFrameCount = decodedFrames,
            PushedFrameCount = pushedFrames,
            DroppedVideoFrameCount = droppedVideoFrames,
            BufferingDecisionCount = bufferingDecisionCount,
            LastVideoBufferedDuration = videoBufferedDuration,
            LowLatencyCatchUpActive = lowLatencyCatchUpActive,
            LastVideoAudioDistance = videoAudioDistance,
            LastAudioClock = audioClock,
            LastFrameAt = lastFrameAt == DateTimeOffset.MinValue ? null : lastFrameAt,
            WorkerRunning = running,
            ExitReason = exitReason,
            LastFFmpegErrorCode = errorCode,
            LastFFmpegError = error,
            HardwareAccelerationRequested = _hardwareAccelerationRequested,
            HardwareAccelerationActive = _hardwareAccelerationActive,
            VideoDecoderBackend = _videoDecoderBackend,
            VideoPresentationBackend = _videoPresentationBackend,
            HardwareAccelerationFailureReason = _hardwareAccelerationFailureReason
        });
    }

    /// <summary>
    /// 从媒体地址提取传输协议。
    /// </summary>
    private static string ProtocolOf(MediaSource source) => Uri.TryCreate(source.Uri, UriKind.Absolute, out var uri) ? uri.Scheme : source.Kind.ToString();

    /// <summary>
    /// 将原生错误码转换为可读信息。
    /// </summary>
    private static string? FormatError(int code) => code < 0 ? FFmpegErrorHelper.GetErrorMessage(code) : null;
}
