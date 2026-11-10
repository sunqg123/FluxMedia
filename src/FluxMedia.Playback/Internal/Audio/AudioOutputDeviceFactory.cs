using System.Runtime.InteropServices;

namespace FluxMedia.Playback.Internal.Audio;

/// <summary>
/// 音频输出设备工厂。Windows 使用 NAudio，Linux 使用系统 OpenAL。
/// </summary>
internal static class AudioOutputDeviceFactory
{
    /// <summary>
    /// 创建对应平台的音频输出设备。
    /// </summary>
    public static IAudioOutputDevice Create(Guid instanceId)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return new NAudioPlaybackOutput(instanceId);
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) || RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return new OpenALPlaybackOutput(instanceId);
        }

        return new NullAudioOutputDevice("当前平台暂未接入播放器音频输出后端。Windows 支持 NAudio，Linux 支持 OpenAL。");
    }
}
