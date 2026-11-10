using System.Threading.Channels;
using FFmpeg.AutoGen;
using FluxMedia.Playback.Internal.Audio;
using FluxMedia.Core.Configuration;
using FluxMedia.Core.Logging;
using FluxMedia.Core.Models;
using FluxMedia.FFmpeg.Native;
using FluxMedia.FFmpeg.Pipeline;

namespace FluxMedia.Playback.Internal;

/// <summary>
/// 消费音频包并输出解码后的 PCM 数据。
/// </summary>
internal sealed unsafe class FFmpegPacketAudioPlaybackWorker : IDisposable
{
    /// <summary>
    /// 跳转预滚容差。
    /// </summary>
    private static readonly TimeSpan SeekPrerollTolerance = TimeSpan.FromMilliseconds(10);

    /// <summary>
    /// 源。
    /// </summary>
    private readonly MediaSource _source;

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
    /// 输出。
    /// </summary>
    private readonly IAudioOutputDevice _output;

    /// <summary>
    /// 读取当前暂停状态的回调。
    /// </summary>
    private readonly Func<bool> _isPaused;

    /// <summary>
    /// 当前音量，范围为 0 到 1。
    /// </summary>
    private readonly Func<double> _volume;

    /// <summary>
    /// 是否处于静音状态。
    /// </summary>
    private readonly Func<bool> _isMuted;

    /// <summary>
    /// 读取最新解码同步请求的回调。
    /// </summary>
    private readonly Func<PlaybackResyncRequest> _resyncRequest;

    /// <summary>
    /// 接收已消费数据包的录制回调。
    /// </summary>
    private readonly FFmpegPacketConsumer? _recordingPacketConsumer;

    /// <summary>
    /// 接收工作线程诊断信息的回调。
    /// </summary>
    private readonly Action<AudioPlaybackDiagnostics>? _onDiagnostics;

    /// <summary>
    /// 是否已释放资源。
    /// </summary>
    private volatile bool _disposed;

    /// <summary>
    /// 新管线目标位置，之前的帧仅用于预滚解码。
    /// </summary>
    private readonly TimeSpan _startPosition;

    /// <summary>
    /// 初始化 FFmpeg 数据包音频播放工作线程所需的状态和资源。
    /// </summary>
    public FFmpegPacketAudioPlaybackWorker(
        MediaSource source, Guid instanceId, TimeSpan startPosition, FFmpegStreamDescriptor stream, ChannelReader<FFmpegPacketLease> packets, Func<long> packetGeneration, IAudioOutputDevice output, Func<bool> isPaused, Func<double> volume, Func<bool> isMuted, Action<AudioPlaybackDiagnostics>? onDiagnostics = null, Func<PlaybackResyncRequest>? resyncRequest = null, FFmpegPacketConsumer? recordingPacketConsumer = null)
    {
        _source = source;
        _instanceId = instanceId;
        _startPosition = startPosition;
        _stream = stream;
        _packets = packets;
        _packetGeneration = packetGeneration;
        _output = output;
        _isPaused = isPaused;
        _volume = volume;
        _isMuted = isMuted;
        _resyncRequest = resyncRequest ?? (() => default);
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
        _output.Stop();
        FluxMediaLog.Write(LogLevel.Information, nameof(FFmpegPacketAudioPlaybackWorker), _instanceId, $"Event=PacketAudioDispose SourceKind={_source.Kind} Url={_source.Uri}");
    }

    /// <summary>
    /// 读取数据包并循环解码。
    /// </summary>
    private void DecodeLoop(CancellationToken cancellationToken)
    {
        var decodedFrames = 0L;
        var writtenBytes = 0L;
        var currentTime = _startPosition;
        var exitReason = "Completed";
        var sampleRate = 0;
        var outputChannels = 0;
        var outputFormat = "s16";

        AVCodecContext* codecContext = null;
        AVFrame* frame = null;
        SwrContext* swr = null;
        byte* outBuffer = null;
        AVChannelLayout outLayout = default;

        try
        {
            var codec = ffmpeg.avcodec_find_decoder(_stream.CodecParameters->codec_id);
            if (codec == null)
            {
                throw new InvalidOperationException("No FFmpeg audio decoder is available for the selected stream.");
            }

            codecContext = ffmpeg.avcodec_alloc_context3(codec);
            if (codecContext == null)
            {
                throw new InvalidOperationException("FFmpeg audio codec context allocation failed.");
            }

            FFmpegErrorHelper.ThrowIfError(ffmpeg.avcodec_parameters_to_context(codecContext, _stream.CodecParameters), "avcodec_parameters_to_context");
            FFmpegErrorHelper.ThrowIfError(ffmpeg.avcodec_open2(codecContext, codec, null), "avcodec_open2");

            sampleRate = codecContext->sample_rate > 0 ? codecContext->sample_rate : 48000;
            outputChannels = codecContext->ch_layout.nb_channels > 0 ? Math.Min((int)codecContext->ch_layout.nb_channels, 2) : 2;
            outputFormat = $"s16/{sampleRate}Hz/{outputChannels}ch";

            ffmpeg.av_channel_layout_default(&outLayout, outputChannels);
            FFmpegErrorHelper.ThrowIfError(
                ffmpeg.swr_alloc_set_opts2(&swr, &outLayout, AVSampleFormat.AV_SAMPLE_FMT_S16, sampleRate, &codecContext->ch_layout, codecContext->sample_fmt, codecContext->sample_rate, 0, null), "swr_alloc_set_opts2");
            FFmpegErrorHelper.ThrowIfError(ffmpeg.swr_init(swr), "swr_init");

            _output.Initialize(sampleRate, outputChannels);
            _output.SetVolume(_volume(), _isMuted());
            _output.Play();
            Report(sampleRate, outputChannels, outputFormat, "Running", decodedFrames, writtenBytes, currentTime, null);
            FluxMediaLog.Write(LogLevel.Information, nameof(FFmpegPacketAudioPlaybackWorker), _instanceId, $"Event=PacketAudioStart SourceKind={_source.Kind} Url={_source.Uri} StreamIndex={_stream.StreamIndex} SampleRate={sampleRate} Channels={outputChannels} Decoder={ffmpeg.avcodec_get_name(_stream.CodecParameters->codec_id)}");

            frame = ffmpeg.av_frame_alloc();
            if (frame == null)
            {
                throw new InvalidOperationException("FFmpeg audio frame allocation failed.");
            }

            var lastStatsAt = DateTimeOffset.UtcNow;
            var wasPaused = false;
            var initialResync = _resyncRequest();
            var lastResyncVersion = initialResync.Version;
            var initialPosition = initialResync.Version > 0 ? initialResync.Position : _startPosition;
            currentTime = initialPosition;
            TimeSpan? prerollDropUntil = initialPosition > TimeSpan.Zero ? initialPosition : null;
            var outData = stackalloc byte*[1];
            foreach (var packetLease in FFmpegPacketReader.ReadWithDrain(_packets, () => _disposed, cancellationToken))
            {
                using (packetLease)
                {
                    ApplyResyncIfNeeded(
                        codecContext, frame, swr, sampleRate, outputChannels, outputFormat, decodedFrames, writtenBytes, ref currentTime, ref lastResyncVersion, ref prerollDropUntil);
                    while (!_disposed && _isPaused() && !cancellationToken.IsCancellationRequested)
                    {
                        ApplyResyncIfNeeded(
                            codecContext, frame, swr, sampleRate, outputChannels, outputFormat, decodedFrames, writtenBytes, ref currentTime, ref lastResyncVersion, ref prerollDropUntil);
                        if (!wasPaused)
                        {
                            wasPaused = true;
                            _output.Pause();
                            Report(sampleRate, outputChannels, outputFormat, "Paused", decodedFrames, writtenBytes, currentTime, null);
                        }

                        Thread.Sleep(20);
                    }

                    if (_disposed || cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }

                    // 同步后只丢弃旧位置的包，保留新位置已经读到的关键包。
                    if (packetLease is not null && packetLease.Generation != _packetGeneration())
                    {
                        FluxMediaLog.Write(LogLevel.Information, nameof(FFmpegPacketAudioPlaybackWorker), _instanceId,
                            $"Event=SeekPacketDiscarded Kind=Audio PacketGeneration={packetLease.Generation} CurrentGeneration={_packetGeneration()}");
                        continue;
                    }

                    if (wasPaused)
                    {
                        wasPaused = false;
                        _output.Play();
                    }

                    if (packetLease is not null) _recordingPacketConsumer?.Invoke((nint)packetLease.Packet);
                    FFmpegErrorHelper.ThrowIfError(ffmpeg.avcodec_send_packet(codecContext, packetLease is null ? null : packetLease.Packet), "avcodec_send_packet");
                    while (!_disposed && !cancellationToken.IsCancellationRequested)
                    {
                        if (ApplyResyncIfNeeded(codecContext, frame, swr, sampleRate, outputChannels, outputFormat, decodedFrames, writtenBytes, ref currentTime, ref lastResyncVersion, ref prerollDropUntil))
                        {
                            break;
                        }

                        var receive = ffmpeg.avcodec_receive_frame(codecContext, frame);
                        if (receive == ffmpeg.AVERROR(ffmpeg.EAGAIN) || receive == ffmpeg.AVERROR_EOF)
                        {
                            break;
                        }

                        FFmpegErrorHelper.ThrowIfError(receive, "avcodec_receive_frame");
                        currentTime = FrameTimestamp(
                            frame, _stream.TimeBase, _stream.StartTime, decodedFrames, sampleRate);
                        if (ShouldDropPrerollFrame(currentTime, ref prerollDropUntil))
                        {
                            decodedFrames++;
                            ffmpeg.av_frame_unref(frame);
                            FluxMediaLog.Write(LogLevel.Debug, nameof(FFmpegPacketAudioPlaybackWorker), _instanceId, $"Event=PacketAudioPrerollDrop Timestamp={currentTime} Target={prerollDropUntil?.ToString() ?? "-"}");
                            continue;
                        }

                        var outSamples = (int)ffmpeg.av_rescale_rnd(ffmpeg.swr_get_delay(swr, codecContext->sample_rate) + frame->nb_samples, sampleRate, codecContext->sample_rate, AVRounding.AV_ROUND_UP);
                        var outBufferSize = ffmpeg.av_samples_get_buffer_size(null, outputChannels, outSamples, AVSampleFormat.AV_SAMPLE_FMT_S16, 1);
                        if (outBufferSize <= 0)
                        {
                            continue;
                        }

                        outBuffer = (byte*)ffmpeg.av_realloc(outBuffer, (ulong)outBufferSize);
                        if (outBuffer == null)
                        {
                            throw new InvalidOperationException("FFmpeg audio conversion buffer allocation failed.");
                        }

                        outData[0] = outBuffer;
                        var converted = ffmpeg.swr_convert(swr, outData, outSamples, frame->extended_data, frame->nb_samples);
                        FFmpegErrorHelper.ThrowIfError(converted, "swr_convert");
                        var bytes = ffmpeg.av_samples_get_buffer_size(null, outputChannels, converted, AVSampleFormat.AV_SAMPLE_FMT_S16, 1);
                        if (bytes > 0)
                        {
                            _output.Write(new ReadOnlySpan<byte>(outBuffer, bytes));
                            writtenBytes += bytes;
                            decodedFrames++;
                        }

                        if (DateTimeOffset.UtcNow - lastStatsAt >= TimeSpan.FromSeconds(1))
                        {
                            lastStatsAt = DateTimeOffset.UtcNow;
                            Report(sampleRate, outputChannels, outputFormat, "Running", decodedFrames, writtenBytes, currentTime, null);
                        }
                    }
                }
            }

            // 排空重采样器，保留末尾不足一帧的采样。
            while (!_disposed && !cancellationToken.IsCancellationRequested)
            {
                const int tailSamples = 4096;
                outBuffer = (byte*)ffmpeg.av_realloc(outBuffer, (ulong)(tailSamples * outputChannels * sizeof(short)));
                if (outBuffer == null) throw new InvalidOperationException("FFmpeg audio conversion buffer allocation failed.");
                outData[0] = outBuffer;
                var converted = ffmpeg.swr_convert(swr, outData, tailSamples, null, 0);
                FFmpegErrorHelper.ThrowIfError(converted, "swr_convert drain");
                if (converted == 0) break;
                var bytes = converted * outputChannels * sizeof(short);
                _output.Write(new ReadOnlySpan<byte>(outBuffer, bytes));
                writtenBytes += bytes;
            }

            // 等待尾音播放完；暂停和停止仍可中断等待。
            while (!_disposed && !cancellationToken.IsCancellationRequested && _output.IsAvailable &&
                _output.GetBufferedDuration() > TimeSpan.Zero)
            {
                var paused = _isPaused();
                if (paused != wasPaused)
                {
                    wasPaused = paused;
                    if (paused) _output.Pause();
                    else _output.Play();
                }
                Thread.Sleep(10);
            }
        }
        catch (OperationCanceledException)
        {
            exitReason = "Canceled";
        }
        catch (Exception ex)
        {
            exitReason = ex.Message;
            FluxMediaLog.Write(LogLevel.Error, nameof(FFmpegPacketAudioPlaybackWorker), _instanceId, $"Event=PacketAudioError SourceKind={_source.Kind} Url={_source.Uri} Error={ex.Message}", ex);
            Report(sampleRate, outputChannels, outputFormat, "Faulted", decodedFrames, writtenBytes, currentTime, ex.Message);
            throw;
        }
        finally
        {
            _output.Stop();
            if (outBuffer != null)
                ffmpeg.av_free(outBuffer);
            if (swr != null)
                ffmpeg.swr_free(&swr);
            ffmpeg.av_channel_layout_uninit(&outLayout);
            if (frame != null)
                ffmpeg.av_frame_free(&frame);
            if (codecContext != null)
                ffmpeg.avcodec_free_context(&codecContext);
            FluxMediaLog.Write(LogLevel.Information, nameof(FFmpegPacketAudioPlaybackWorker), _instanceId, $"Event=PacketAudioExit SourceKind={_source.Kind} Url={_source.Uri} Reason={exitReason} DecodedFrames={decodedFrames} WrittenPcmBytes={writtenBytes} CurrentTime={currentTime}");
            Report(sampleRate, outputChannels, outputFormat, "Exited", decodedFrames, writtenBytes, currentTime, null);
        }
    }

    /// <summary>
    /// 发现新的同步请求时重置解码状态。
    /// </summary>
    private bool ApplyResyncIfNeeded(
        AVCodecContext* codecContext, AVFrame* frame, SwrContext* swr, int sampleRate, int channels, string outputFormat, long decodedFrames, long writtenBytes, ref TimeSpan currentTime, ref long lastResyncVersion, ref TimeSpan? prerollDropUntil)
    {
        var request = _resyncRequest();
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

        if (swr != null)
        {
            ffmpeg.swr_init(swr);
        }

        _output.Clear();
        currentTime = request.Position;
        prerollDropUntil = request.Position;
        Report(sampleRate, channels, outputFormat, "SeekResync", decodedFrames, writtenBytes, currentTime, null);
        FluxMediaLog.Write(LogLevel.Information, nameof(FFmpegPacketAudioPlaybackWorker), _instanceId, $"Event=PacketAudioDecoderResync Version={request.Version} Position={request.Position}");
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
    /// 输出状态。
    /// </summary>
    private string OutputState => _output.IsAvailable ? $"Available:{_output.BackendName}" : $"Unavailable:{_output.LastError ?? "-"}";

    /// <summary>
    /// 上报音频解码和输出状态。
    /// </summary>
    private void Report(int sampleRate, int channels, string outputFormat, string state, long decodedFrames, long writtenBytes, TimeSpan currentTime, string? error)
    {
        var outputDiagnostic = _output.GetDiagnosticInfo();
        _onDiagnostics?.Invoke(new AudioPlaybackDiagnostics
        {
            StreamIndex = _stream.StreamIndex,
            SampleRate = sampleRate > 0 ? sampleRate : null,
            Channels = channels > 0 ? channels : null,
            OutputFormat = outputFormat,
            WorkerState = state,
            DecodedFrameCount = decodedFrames,
            WrittenPcmBytes = writtenBytes,
            CurrentTime = currentTime,
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
    private static TimeSpan FrameTimestamp(
        AVFrame* frame, AVRational timeBase, TimeSpan timestampOrigin, long sequence, int sampleRate)
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
