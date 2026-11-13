using FFmpeg.AutoGen;
using FluxMedia.Core.Models;
using FluxMedia.Playback;

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
    /// 借用的录制器，由会话录制控制器负责释放。
    /// </summary>
    private FFmpegPacketRemuxRecorder? _recording;

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
    /// 根据已打开的流创建录制器，所有权交给调用方。
    /// </summary>
    public FFmpegPacketRemuxRecorder CreateRecorder(PlaybackRecordingOptions options)
    {
        ThrowIfDisposed();
        if (VideoStream is null && AudioStream is null)
            throw new InvalidOperationException("No audio or video streams are available for recording.");
        return new FFmpegPacketRemuxRecorder(options, _instanceId, VideoStream, AudioStream);
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
    /// 解除借用关系，不释放录制器。
    /// </summary>
    public void DetachRecording()
    {
        lock (_recordingSync)
        {
            _recording = null;
        }
    }

    /// <summary>
    /// 关联当前录制器并准备写入播放数据包。
    /// </summary>
    public bool AttachRecording(FFmpegPacketRemuxRecorder recording)
    {
        if (recording is null)
        {
            throw new ArgumentNullException(nameof(recording));
        }
        ThrowIfDisposed();
        lock (_recordingSync)
        {
            if (ReferenceEquals(_recording, recording))
                return false;
            if (_recording is not null)
            {
                throw new InvalidOperationException("A recording is already attached to this demuxer.");
            }

            _recording = recording;
            FlushRecordingPreroll(recording);
            return true;
        }
    }

    /// <summary>
    /// 缓存录制所需的起始关键帧及相邻数据包。
    /// </summary>
    private unsafe void CacheRecordingPreroll(AVPacket* packet)
    {
        if (_source.Kind != MediaSourceKind.File || packet == null || VideoStream is null)
            return;
        if (packet->stream_index == VideoStream.StreamIndex)
        {
            if ((packet->flags & ffmpeg.AV_PKT_FLAG_KEY) != 0)
            {
                ClearRecordingPreroll();
                _recordingPrerollVideoKeyUs = PacketTimestampUs(packet, VideoStream.TimeBase);
            }

            if (_recordingPrerollVideoKeyUs is null)
                return;
        }
        else if (AudioStream is not null && packet->stream_index == AudioStream.StreamIndex)
        {
            if (_recordingPrerollVideoKeyUs is null)
                return;
            var audioTimestampUs = PacketTimestampUs(packet, AudioStream.TimeBase);
            if (audioTimestampUs is not null && audioTimestampUs < _recordingPrerollVideoKeyUs)
                return;
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
        if (_recordingPrerollPackets.Count == 0)
            return;
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
