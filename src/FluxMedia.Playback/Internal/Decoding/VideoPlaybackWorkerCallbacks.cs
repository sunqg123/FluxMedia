using FluxMedia.Core.Models;
using FluxMedia.FFmpeg.Pipeline;

namespace FluxMedia.Playback.Internal;

/// <summary>
/// 提供视频输出、同步参考和通知入口。
/// </summary>
internal sealed class VideoPlaybackWorkerCallbacks
{
    /// <summary>
    /// 绑定本代管线的依赖，不接管资源释放。
    /// </summary>
    public VideoPlaybackWorkerCallbacks(Action<VideoFrame> frameReady, Func<AudioClockSnapshot?> audioClock, Func<TimeSpan> bufferedDuration, Func<int> bufferedPacketCount, Func<VideoOutputCapabilities> videoCapabilities, FFmpegPacketConsumer? packetConsumed = null)
    {
        FrameReady = frameReady ?? throw new ArgumentNullException(nameof(frameReady));
        AudioClock = audioClock ?? throw new ArgumentNullException(nameof(audioClock));
        BufferedDuration = bufferedDuration ?? throw new ArgumentNullException(nameof(bufferedDuration));
        BufferedPacketCount = bufferedPacketCount ?? throw new ArgumentNullException(nameof(bufferedPacketCount));
        VideoCapabilities = videoCapabilities ?? throw new ArgumentNullException(nameof(videoCapabilities));
        PacketConsumed = packetConsumed;
    }

    /// <summary>
    /// 接收解码后的有效视频帧。
    /// </summary>
    public Action<VideoFrame> FrameReady { get; }

    /// <summary>
    /// 读取最新音频时钟信息。
    /// </summary>
    public Func<AudioClockSnapshot?> AudioClock { get; }

    /// <summary>
    /// 读取当前视频缓冲时长。
    /// </summary>
    public Func<TimeSpan> BufferedDuration { get; }

    /// <summary>
    /// 读取当前视频缓冲包数。
    /// </summary>
    public Func<int> BufferedPacketCount { get; }

    /// <summary>
    /// 读取显示端输出能力。
    /// </summary>
    public Func<VideoOutputCapabilities> VideoCapabilities { get; }

    /// <summary>
    /// 通知已消费的录制数据包。
    /// </summary>
    public FFmpegPacketConsumer? PacketConsumed { get; }
}
