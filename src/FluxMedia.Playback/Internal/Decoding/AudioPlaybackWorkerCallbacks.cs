using FluxMedia.FFmpeg.Pipeline;

namespace FluxMedia.Playback.Internal;

/// <summary>
/// 提供音频控制和通知入口。
/// </summary>
internal sealed class AudioPlaybackWorkerCallbacks
{
    /// <summary>
    /// 绑定本代管线的依赖，不接管资源释放。
    /// </summary>
    public AudioPlaybackWorkerCallbacks(Func<double> volume, Func<bool> isMuted, Action<AudioPlaybackDiagnostics> diagnostics, FFmpegPacketConsumer? packetConsumed = null)
    {
        Volume = volume ?? throw new ArgumentNullException(nameof(volume));
        IsMuted = isMuted ?? throw new ArgumentNullException(nameof(isMuted));
        Diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
        PacketConsumed = packetConsumed;
    }

    /// <summary>
    /// 读取当前音量。
    /// </summary>
    public Func<double> Volume { get; }

    /// <summary>
    /// 读取当前静音状态。
    /// </summary>
    public Func<bool> IsMuted { get; }

    /// <summary>
    /// 上报音频状态与时钟。
    /// </summary>
    public Action<AudioPlaybackDiagnostics> Diagnostics { get; }

    /// <summary>
    /// 通知已消费的录制数据包。
    /// </summary>
    public FFmpegPacketConsumer? PacketConsumed { get; }
}
