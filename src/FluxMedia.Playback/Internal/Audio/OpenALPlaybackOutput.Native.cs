using System.Runtime.InteropServices;

namespace FluxMedia.Playback.Internal.Audio;

// 按平台分发 OpenAL 原生调用。
internal sealed partial class OpenALPlaybackOutput
{
    /// <summary>
    /// 按平台调用 OpenAL 原生接口。
    /// </summary>
    private static partial class OpenALNative
    {
        /// <summary>
        /// 是否 macOS 操作系统。
        /// </summary>
        private static bool IsMacOS => RuntimeInformation.IsOSPlatform(OSPlatform.OSX);

        /// <summary>
        /// 调用 OpenAL 打开音频设备。
        /// </summary>
        internal static IntPtr AlcOpenDevice(IntPtr deviceName) => IsMacOS ? MacOS.AlcOpenDevice(deviceName) : Linux.AlcOpenDevice(deviceName);

        /// <summary>
        /// 为音频设备创建 OpenAL 上下文。
        /// </summary>
        internal static IntPtr AlcCreateContext(IntPtr device, IntPtr attributes) => IsMacOS ? MacOS.AlcCreateContext(device, attributes) : Linux.AlcCreateContext(device, attributes);

        /// <summary>
        /// 将 OpenAL 上下文绑定到当前线程。
        /// </summary>
        internal static byte AlcMakeContextCurrent(IntPtr context) => IsMacOS ? MacOS.AlcMakeContextCurrent(context) : Linux.AlcMakeContextCurrent(context);

        /// <summary>
        /// 销毁 OpenAL 音频上下文。
        /// </summary>
        internal static void AlcDestroyContext(IntPtr context)
        {
            if (IsMacOS)
            {
                MacOS.AlcDestroyContext(context);
            }
            else
            {
                Linux.AlcDestroyContext(context);
            }
        }

        /// <summary>
        /// 关闭 OpenAL 音频设备。
        /// </summary>
        internal static byte AlcCloseDevice(IntPtr device) => IsMacOS ? MacOS.AlcCloseDevice(device) : Linux.AlcCloseDevice(device);

        /// <summary>
        /// 创建 OpenAL 音频源。
        /// </summary>
        internal static void AlGenSources(int count, out uint source)
        {
            if (IsMacOS)
            {
                MacOS.AlGenSources(count, out source);
            }
            else
            {
                Linux.AlGenSources(count, out source);
            }
        }

        /// <summary>
        /// 删除 OpenAL 音频源。
        /// </summary>
        internal static void AlDeleteSources(int count, ref uint source)
        {
            if (IsMacOS)
            {
                MacOS.AlDeleteSources(count, ref source);
            }
            else
            {
                Linux.AlDeleteSources(count, ref source);
            }
        }

        /// <summary>
        /// 启动 OpenAL 音频源播放。
        /// </summary>
        internal static void AlSourcePlay(uint source)
        {
            if (IsMacOS)
            {
                MacOS.AlSourcePlay(source);
            }
            else
            {
                Linux.AlSourcePlay(source);
            }
        }

        /// <summary>
        /// 暂停 OpenAL 音频源。
        /// </summary>
        internal static void AlSourcePause(uint source)
        {
            if (IsMacOS)
            {
                MacOS.AlSourcePause(source);
            }
            else
            {
                Linux.AlSourcePause(source);
            }

        }

        /// <summary>
        /// 停止 OpenAL 音频源。
        /// </summary>
        internal static void AlSourceStop(uint source)
        {
            if (IsMacOS)
            {
                MacOS.AlSourceStop(source);
            }
            else
            {
                Linux.AlSourceStop(source);
            }
        }

        /// <summary>
        /// 设置 OpenAL 音频源的浮点参数。
        /// </summary>
        internal static void AlSourcef(uint source, int parameter, float value)
        {
            if (IsMacOS)
            {
                MacOS.AlSourcef(source, parameter, value);
            }
            else
            {
                Linux.AlSourcef(source, parameter, value);
            }
        }
        /// <summary>
        /// 读取 OpenAL 音频源的整数状态。
        /// </summary>
        internal static void AlGetSourcei(uint source, int parameter, out int value)
        {
            if (IsMacOS)
            {
                MacOS.AlGetSourcei(source, parameter, out value);
            }
            else
            {
                Linux.AlGetSourcei(source, parameter, out value);
            }
        }

        /// <summary>
        /// 创建 OpenAL 音频缓冲区。
        /// </summary>
        internal static void AlGenBuffers(int count, out uint buffer)
        {
            if (IsMacOS)
            {
                MacOS.AlGenBuffers(count, out buffer);
            }
            else
            {
                Linux.AlGenBuffers(count, out buffer);
            }
        }

        /// <summary>
        /// 删除 OpenAL 音频缓冲区。
        /// </summary>
        internal static void AlDeleteBuffers(int count, ref uint buffer)
        {
            if (IsMacOS)
            {
                MacOS.AlDeleteBuffers(count, ref buffer);
            }
            else
            {
                Linux.AlDeleteBuffers(count, ref buffer);
            }
        }

        /// <summary>
        /// 将 PCM 数据上传到 OpenAL 缓冲区。
        /// </summary>
        internal static void AlBufferData(uint buffer, int format, byte[] data, int size, int frequency)
        {
            if (IsMacOS)
            {
                MacOS.AlBufferData(buffer, format, data, size, frequency);
            }
            else
            {
                Linux.AlBufferData(buffer, format, data, size, frequency);
            }
        }

        /// <summary>
        /// 将缓冲区加入 OpenAL 音频源队列。
        /// </summary>
        internal static void AlSourceQueueBuffers(uint source, int count, ref uint buffer)
        {
            if (IsMacOS)
            {
                MacOS.AlSourceQueueBuffers(source, count, ref buffer);
            }
            else
            {
                Linux.AlSourceQueueBuffers(source, count, ref buffer);
            }
        }

        /// <summary>
        /// 从 OpenAL 音频源取出已处理缓冲区。
        /// </summary>
        internal static void AlSourceUnqueueBuffers(uint source, int count, out uint buffer)
        {
            if (IsMacOS)
            {
                MacOS.AlSourceUnqueueBuffers(source, count, out buffer);
            }
            else
            {
                Linux.AlSourceUnqueueBuffers(source, count, out buffer);
            }
        }

        /// <summary>
        /// 读取并清除当前 OpenAL 错误码。
        /// </summary>
        internal static int AlGetError() => IsMacOS ? MacOS.AlGetError() : Linux.AlGetError();
    }
}
