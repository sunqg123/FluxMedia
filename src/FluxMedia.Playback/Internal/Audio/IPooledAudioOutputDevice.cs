namespace FluxMedia.Playback.Internal.Audio;

/// <summary>
/// 统计音频缓冲区的复用情况。
/// </summary>
internal interface IPooledAudioOutputDevice
{
    /// <summary>
    /// 写入数据供后续处理。
    /// </summary>
    void Write(byte[] pcm, int length);
}
