namespace FluxMedia.Playback.Internal.Audio;

/// <summary>
/// 接收变速后的交错 PCM。
/// </summary>
internal delegate void TempoPcmConsumer(ReadOnlySpan<byte> pcm, int samples, double rate);
