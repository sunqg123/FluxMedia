namespace FluxMedia.FFmpeg.Pipeline;

/// <summary>
/// 接收原生数据包指针的回调。
/// </summary>
internal delegate void FFmpegPacketConsumer(nint packet);
