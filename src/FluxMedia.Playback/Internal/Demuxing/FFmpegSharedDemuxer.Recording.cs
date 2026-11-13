using FFmpeg.AutoGen;
using FluxMedia.Core.Configuration;
using FluxMedia.Core.Logging;
using FluxMedia.Core.Models;
using FluxMedia.Playback;
using FluxMedia.Playback.Internal;

namespace FluxMedia.FFmpeg.Pipeline;

// 原生指针仅用于同步方法，异步收尾不进入 unsafe 作用域。
internal sealed partial class FFmpegSharedDemuxer
{
    /// <summary>
    /// 保护录制器及预滚缓存的锁。
    /// </summary>
    private readonly object _recordingSync = new();

    /// <summary>
    /// 录制开始前缓存的关键帧及后续数据包。
    /// </summary>
    private readonly List<FFmpegPacketLease> _recordingPrerollPackets = new();

    /// <summary>
    /// 录制。
    /// </summary>
    private FFmpegPacketRemuxRecorder? _recording;

    /// <summary>
    /// 待处理录制配置。
    /// </summary>
    private PlaybackRecordingOptions? _pendingRecordingOptions;

    /// <summary>
    /// 录制预滚字节。
    /// </summary>
    private long _recordingPrerollBytes;

    /// <summary>
    /// 预滚视频关键帧的时间（微秒）。
    /// </summary>
    private long? _recordingPrerollVideoKeyUs;

    /// <summary>
    /// 最大录制预滚字节。
    /// </summary>
    private const long MaxRecordingPrerollBytes = 64L * 1024 * 1024;

    /// <summary>
    /// 录制实际结果。
    /// </summary>
    public PlaybackRecordingResult? RecordingResult
    {
        get { lock (_recordingSync) return _recording?.Result; }
    }

    /// <summary>
    /// 启动当前媒体的包级录制。
    /// </summary>
    public void StartRecording(PlaybackRecordingOptions options)
    {
        ThrowIfDisposed();
        _pendingRecordingOptions = options;
        if (VideoStream is null && AudioStream is null) return;
        StartPendingRecordingIfNeeded();
    }

    /// <summary>
    /// 将已消费的播放数据包交给录制器。
    /// </summary>
    public unsafe void WriteRecordingPacket(nint packet)
    {
        FFmpegPacketRemuxRecorder? recording;
        lock (_recordingSync)
        {
            recording = _recording;
            if (recording is null)
            {
                CacheRecordingPreroll((AVPacket*)packet);
                return;
            }
        }
        recording?.TryWrite((AVPacket*)packet);
    }

    /// <summary>
    /// 时间线跳变前暂停录制写入。
    /// </summary>
    public void SuspendRecordingForDiscontinuity()
    {
        FFmpegPacketRemuxRecorder? recording;
        lock (_recordingSync)
        {
            recording = _recording;
        }
        recording?.SuspendForDiscontinuity();
    }

    /// <summary>
    /// 时间线跳变后恢复录制写入。
    /// </summary>
    public void ResumeRecordingAfterDiscontinuity()
    {
        FFmpegPacketRemuxRecorder? recording;
        lock (_recordingSync)
        {
            recording = _recording;
        }
        recording?.ResumeAfterDiscontinuity();
    }

    /// <summary>
    /// 解除录制关联并返回录制结果。
    /// </summary>
    public FFmpegPacketRemuxRecorder? DetachRecording()
    {
        lock (_recordingSync)
        {
            var recording = _recording;
            _recording = null;
            return recording;
        }
    }

    /// <summary>
    /// 关联当前录制器并准备写入播放数据包。
    /// </summary>
    public void AttachRecording(FFmpegPacketRemuxRecorder recording)
    {
        if (recording is null)
        {
            throw new ArgumentNullException(nameof(recording));
        }
        ThrowIfDisposed();
        lock (_recordingSync)
        {
            if (_recording is not null)
            {
                throw new InvalidOperationException("A recording is already attached to this demuxer.");
            }

            _recording = recording;
            _pendingRecordingOptions = null;
            FlushRecordingPreroll(recording);
        }
    }

    /// <summary>
    /// 等待录制排空、封装并释放，再解除关联。
    /// </summary>
    public async ValueTask<PlaybackRecordingResult?> StopRecordingAsync()
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
            var result = recording.Result;
            return failure is null ? result : result with
            {
                State = PlaybackRecordingState.Failed,
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

    /// <summary>
    /// 具备流信息后启动等待中的录制。
    /// </summary>
    private void StartPendingRecordingIfNeeded()
    {
        var options = _pendingRecordingOptions;
        if (options is null || _recording is not null) return;
        if (VideoStream is null && AudioStream is null)
        {
            throw new InvalidOperationException("No audio or video streams are available for recording.");
        }

        var recording = new FFmpegPacketRemuxRecorder(options, _instanceId, VideoStream, AudioStream);
        recording.Start();
        lock (_recordingSync)
        {
            _recording = recording;
            _pendingRecordingOptions = null;
            FlushRecordingPreroll(recording);
        }
    }

    /// <summary>
    /// 缓存录制所需的起始关键帧及相邻数据包。
    /// </summary>
    private unsafe void CacheRecordingPreroll(AVPacket* packet)
    {
        if (_source.Kind != MediaSourceKind.File || packet == null || VideoStream is null) return;
        if (packet->stream_index == VideoStream.StreamIndex)
        {
            if ((packet->flags & ffmpeg.AV_PKT_FLAG_KEY) != 0)
            {
                ClearRecordingPreroll();
                _recordingPrerollVideoKeyUs = PacketTimestampUs(packet, VideoStream.TimeBase);
            }

            if (_recordingPrerollVideoKeyUs is null) return;
        }
        else if (AudioStream is not null && packet->stream_index == AudioStream.StreamIndex)
        {
            if (_recordingPrerollVideoKeyUs is null) return;
            var audioTimestampUs = PacketTimestampUs(packet, AudioStream.TimeBase);
            if (audioTimestampUs is not null && audioTimestampUs < _recordingPrerollVideoKeyUs) return;
        }
        else
        {
            return;
        }

        var clone = FFmpegPacketLease.Clone(packet, PacketGeneration);
        _recordingPrerollPackets.Add(clone);
        _recordingPrerollBytes += Math.Max(0, clone.Packet->size);
        if (_recordingPrerollBytes > MaxRecordingPrerollBytes)
        {
            ClearRecordingPreroll();
        }
    }

    /// <summary>
    /// 将录制预滚包写入输出文件。
    /// </summary>
    private unsafe void FlushRecordingPreroll(FFmpegPacketRemuxRecorder recording)
    {
        if (_recordingPrerollPackets.Count == 0) return;
        var packets = _recordingPrerollPackets.OrderBy(packet => RecordingPacketTimestampUs(packet.Packet)).ToArray();
        _recordingPrerollPackets.Clear();
        _recordingPrerollBytes = 0;
        _recordingPrerollVideoKeyUs = null;
        foreach (var packet in packets)
        {
            using (packet)
            {
                recording.TryWrite(packet.Packet);
            }
        }

        FluxMediaLog.Write(
            LogLevel.Information, nameof(FFmpegSharedDemuxer), _instanceId, $"Event=RecordingPrerollFlushed Packets={packets.Length} Source={_source.Uri}");
    }

    /// <summary>
    /// 获取录制使用的数据包时间戳。
    /// </summary>
    private unsafe long RecordingPacketTimestampUs(AVPacket* packet)
    {
        var timeBase = VideoStream is not null && packet->stream_index == VideoStream.StreamIndex
            ? VideoStream.TimeBase
            : AudioStream?.TimeBase ?? new AVRational { num = 1, den = ffmpeg.AV_TIME_BASE };
        return PacketTimestampUs(packet, timeBase) ?? long.MaxValue;
    }

    /// <summary>
    /// 将数据包时间戳换算为微秒。
    /// </summary>
    private static unsafe long? PacketTimestampUs(AVPacket* packet, AVRational timeBase)
    {
        var timestamp = packet->dts != ffmpeg.AV_NOPTS_VALUE ? packet->dts : packet->pts;
        return timestamp == ffmpeg.AV_NOPTS_VALUE
            ? null
            : ffmpeg.av_rescale_q(timestamp, timeBase, new AVRational { num = 1, den = ffmpeg.AV_TIME_BASE });
    }

    /// <summary>
    /// 释放录制预滚缓存。
    /// </summary>
    private void ClearRecordingPreroll()
    {
        foreach (var packet in _recordingPrerollPackets)
        {
            packet.Dispose();
        }

        _recordingPrerollPackets.Clear();
        _recordingPrerollBytes = 0;
        _recordingPrerollVideoKeyUs = null;
    }
}
