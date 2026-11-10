namespace FluxMedia.Playback.Internal.Audio;

/// <summary>
/// PCM 输出格式描述。
/// </summary>
/// <param name="SampleRate">音频采样率，单位为赫兹。</param>
/// <param name="Channels">音频声道数。</param>
/// <param name="BitsPerSample">每个音频采样使用的位数。</param>
internal sealed record PcmAudioFormat(int SampleRate, int Channels, int BitsPerSample = 16);
