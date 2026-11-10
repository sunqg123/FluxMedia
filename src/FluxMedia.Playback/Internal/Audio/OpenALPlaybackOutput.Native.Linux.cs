using System.Runtime.InteropServices;

namespace FluxMedia.Playback.Internal.Audio;

// 声明 Linux 的 OpenAL 原生接口。
internal sealed partial class OpenALPlaybackOutput
{
    private static partial class OpenALNative
    {
        /// <summary>
        /// Linux 平台的 OpenAL 接口。
        /// </summary>
        private static class Linux
        {
            /// <summary>
            /// 原生库名称。
            /// </summary>
            private const string LibraryName = "libopenal.so.1";

            /// <summary>
            /// 调用 OpenAL 打开音频设备。
            /// </summary>
            [DllImport(LibraryName, EntryPoint = "alcOpenDevice")] internal static extern IntPtr AlcOpenDevice(IntPtr deviceName);

            /// <summary>
            /// 为音频设备创建 OpenAL 上下文。
            /// </summary>
            [DllImport(LibraryName, EntryPoint = "alcCreateContext")] internal static extern IntPtr AlcCreateContext(IntPtr device, IntPtr attributes);

            /// <summary>
            /// 将 OpenAL 上下文绑定到当前线程。
            /// </summary>
            [DllImport(LibraryName, EntryPoint = "alcMakeContextCurrent")] internal static extern byte AlcMakeContextCurrent(IntPtr context);

            /// <summary>
            /// 销毁 OpenAL 音频上下文。
            /// </summary>
            [DllImport(LibraryName, EntryPoint = "alcDestroyContext")] internal static extern void AlcDestroyContext(IntPtr context);

            /// <summary>
            /// 关闭 OpenAL 音频设备。
            /// </summary>
            [DllImport(LibraryName, EntryPoint = "alcCloseDevice")] internal static extern byte AlcCloseDevice(IntPtr device);

            /// <summary>
            /// 创建 OpenAL 音频源。
            /// </summary>
            [DllImport(LibraryName, EntryPoint = "alGenSources")] internal static extern void AlGenSources(int count, out uint source);

            /// <summary>
            /// 删除 OpenAL 音频源。
            /// </summary>
            [DllImport(LibraryName, EntryPoint = "alDeleteSources")] internal static extern void AlDeleteSources(int count, ref uint source);

            /// <summary>
            /// 启动 OpenAL 音频源播放。
            /// </summary>
            [DllImport(LibraryName, EntryPoint = "alSourcePlay")] internal static extern void AlSourcePlay(uint source);

            /// <summary>
            /// 暂停 OpenAL 音频源。
            /// </summary>
            [DllImport(LibraryName, EntryPoint = "alSourcePause")] internal static extern void AlSourcePause(uint source);

            /// <summary>
            /// 停止 OpenAL 音频源。
            /// </summary>
            [DllImport(LibraryName, EntryPoint = "alSourceStop")] internal static extern void AlSourceStop(uint source);

            /// <summary>
            /// 设置 OpenAL 音频源的浮点参数。
            /// </summary>
            [DllImport(LibraryName, EntryPoint = "alSourcef")] internal static extern void AlSourcef(uint source, int parameter, float value);

            /// <summary>
            /// 读取 OpenAL 音频源的整数状态。
            /// </summary>
            [DllImport(LibraryName, EntryPoint = "alGetSourcei")] internal static extern void AlGetSourcei(uint source, int parameter, out int value);

            /// <summary>
            /// 创建 OpenAL 音频缓冲区。
            /// </summary>
            [DllImport(LibraryName, EntryPoint = "alGenBuffers")] internal static extern void AlGenBuffers(int count, out uint buffer);

            /// <summary>
            /// 删除 OpenAL 音频缓冲区。
            /// </summary>
            [DllImport(LibraryName, EntryPoint = "alDeleteBuffers")] internal static extern void AlDeleteBuffers(int count, ref uint buffer);

            /// <summary>
            /// 将 PCM 数据上传到 OpenAL 缓冲区。
            /// </summary>
            [DllImport(LibraryName, EntryPoint = "alBufferData")] internal static extern void AlBufferData(uint buffer, int format, byte[] data, int size, int frequency);

            /// <summary>
            /// 将缓冲区加入 OpenAL 音频源队列。
            /// </summary>
            [DllImport(LibraryName, EntryPoint = "alSourceQueueBuffers")] internal static extern void AlSourceQueueBuffers(uint source, int count, ref uint buffer);

            /// <summary>
            /// 从 OpenAL 音频源取出已处理缓冲区。
            /// </summary>
            [DllImport(LibraryName, EntryPoint = "alSourceUnqueueBuffers")] internal static extern void AlSourceUnqueueBuffers(uint source, int count, out uint buffer);

            /// <summary>
            /// 读取并清除当前 OpenAL 错误码。
            /// </summary>
            [DllImport(LibraryName, EntryPoint = "alGetError")] internal static extern int AlGetError();
        }
    }
}
