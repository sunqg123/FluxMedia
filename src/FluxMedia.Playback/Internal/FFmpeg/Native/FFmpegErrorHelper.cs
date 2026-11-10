using FFmpeg.AutoGen;
using System.Text;

namespace FluxMedia.FFmpeg.Native;

/// <summary>
/// FFmpeg 错误码转换工具。
/// </summary>
internal static unsafe class FFmpegErrorHelper
{
    /// <summary>
    /// 获取 FFmpeg 错误码对应的说明。
    /// </summary>
    public static string GetErrorMessage(int errorCode)
    {
        const int bufferSize = 1024;
        var buffer = stackalloc byte[bufferSize];
        var result = ffmpeg.av_strerror(errorCode, buffer, (ulong)bufferSize);
        if (result < 0)
        {
            return $"未知 FFmpeg 错误：{errorCode}";
        }

        var length = 0;
        while (length < bufferSize && buffer[length] != 0)
        {
            length++;
        }

        return length == 0 ? $"未知 FFmpeg 错误：{errorCode}" : Encoding.UTF8.GetString(buffer, length);
    }

    /// <summary>
    /// 返回错误码时抛出包含说明的异常。
    /// </summary>
    public static void ThrowIfError(int errorCode, string operationName)
    {
        if (errorCode >= 0) return;
        throw new InvalidOperationException($"{operationName} 失败。FFmpegError={errorCode}, Message={GetErrorMessage(errorCode)}");
    }
}
