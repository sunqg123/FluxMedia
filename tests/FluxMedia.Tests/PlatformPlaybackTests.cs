using FluxMedia.Playback;
using FluxMedia.Playback.Internal.Audio;
using System.Runtime.InteropServices;

namespace FluxMedia.Tests;

/// <summary>
/// 平台播放测试。
/// </summary>
public sealed class PlatformPlaybackTests
{
    /// <summary>
    /// 验证音频工厂按当前平台选择输出后端。
    /// </summary>
    [Fact]
    public void AudioOutputFactory_SelectsCurrentPlatformBackend()
    {
        using var output = AudioOutputDeviceFactory.Create(Guid.NewGuid());

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Assert.IsType<NAudioPlaybackOutput>(output);
            return;
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ||
            RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            Assert.IsType<OpenALPlaybackOutput>(output);
            return;
        }

        Assert.IsType<NullAudioOutputDevice>(output);
    }

    /// <summary>
    /// 验证 OpenAL 初始化前重复释放仍然安全。
    /// </summary>
    [Fact]
    public void OpenALOutput_DisposeBeforeInitialize_IsIdempotent()
    {
        var output = new OpenALPlaybackOutput(Guid.NewGuid());

        output.Dispose();
        output.Dispose();

        Assert.Equal(AudioOutputState.Disposed, output.State);
        Assert.False(output.IsAvailable);
    }

    /// <summary>
    /// 目录缺少任一绑定依赖时不能被选为原生库目录。
    /// </summary>
    [Fact]
    public void FFmpegDirectory_RequiresCompleteBindingLibrarySet()
    {
        var directory = Path.Combine(Path.GetTempPath(), "FluxMedia-library-discovery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var names = FFmpegLibraries.RequiredFileNames;
            Assert.NotEmpty(names);
            Assert.False(FFmpegLibraries.IsAvailable(directory));
            foreach (var name in names.Take(names.Count - 1))
                File.WriteAllBytes(Path.Combine(directory, name), Array.Empty<byte>());
            Assert.False(FFmpegLibraries.IsAvailable(directory));
            File.WriteAllBytes(Path.Combine(directory, names[names.Count - 1]), Array.Empty<byte>());
            Assert.True(FFmpegLibraries.IsAvailable(directory));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
