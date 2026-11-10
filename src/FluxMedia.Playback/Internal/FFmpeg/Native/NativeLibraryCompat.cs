using System.ComponentModel;
using System.Runtime.InteropServices;

namespace FluxMedia.FFmpeg.Native;

/// <summary>
/// 封装各平台的原生库加载与符号查找。
/// </summary>
internal static class NativeLibraryCompat
{
    /// <summary>
    /// 立即解析动态库符号的标志。
    /// </summary>
    private const int RtldNow = 2;

    /// <summary>
    /// Linux 全局导出动态库符号的标志。
    /// </summary>
    private const int RtldGlobalLinux = 0x100;

    /// <summary>
    /// 进程内按绝对路径共享的动态库句柄。
    /// </summary>
    private static readonly Dictionary<string, IntPtr> Handles = new(
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    /// <summary>
    /// 同步锁。
    /// </summary>
    private static readonly object SyncRoot = new();

    /// <summary>
    /// 加载指定路径的原生库并返回句柄。
    /// </summary>
    public static void Load(string path)
    {
        path = Path.GetFullPath(path);
        lock (SyncRoot)
        {
            if (Handles.ContainsKey(path)) return;
            IntPtr handle;
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                handle = LoadLibraryW(path);
                if (handle == IntPtr.Zero)
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), $"无法加载原生库：{path}");
                }
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                handle = DlopenMac(path, RtldNow);
                if (handle == IntPtr.Zero)
                {
                    throw new InvalidOperationException($"无法加载原生库：{path}");
                }
            }
            else
            {
                handle = DlopenLinux(path, RtldNow | RtldGlobalLinux);
                if (handle == IntPtr.Zero)
                {
                    throw new InvalidOperationException($"无法加载原生库：{path}。dlerror={GetLinuxDlError() ?? "未知"}");
                }
            }

            // FFmpeg 的函数绑定缓存贯穿进程；每个路径只加载一次。
            Handles.Add(path, handle);
        }
    }

    /// <summary>
    /// 调用 Windows 接口加载原生库。
    /// </summary>
    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibraryW(string fileName);

    /// <summary>
    /// 在 Linux 上加载原生动态库。
    /// </summary>
    private static IntPtr DlopenLinux(string fileName, int flags)
    {
        try
        {
            return DlopenLinuxGlibc(fileName, flags);
        }
        catch (DllNotFoundException)
        {
            return DlopenLinuxGeneric(fileName, flags);
        }
    }

    /// <summary>
    /// 获取 Linux 动态库加载失败原因。
    /// </summary>
    private static string? GetLinuxDlError()
    {
        IntPtr error;
        try
        {
            error = DlerrorLinuxGlibc();
        }
        catch (DllNotFoundException)
        {
            error = DlerrorLinuxGeneric();
        }

        return error == IntPtr.Zero ? null : Marshal.PtrToStringAnsi(error);
    }

    /// <summary>
    /// 通过 glibc 加载 Linux 动态库。
    /// </summary>
    [DllImport("libdl.so.2", EntryPoint = "dlopen")]
    private static extern IntPtr DlopenLinuxGlibc(string fileName, int flags);

    /// <summary>
    /// 通过通用接口加载 Linux 动态库。
    /// </summary>
    [DllImport("libdl.so", EntryPoint = "dlopen")]
    private static extern IntPtr DlopenLinuxGeneric(string fileName, int flags);

    /// <summary>
    /// 通过 glibc 获取动态库错误信息。
    /// </summary>
    [DllImport("libdl.so.2", EntryPoint = "dlerror")]
    private static extern IntPtr DlerrorLinuxGlibc();

    /// <summary>
    /// 通过通用接口获取动态库错误信息。
    /// </summary>
    [DllImport("libdl.so", EntryPoint = "dlerror")]
    private static extern IntPtr DlerrorLinuxGeneric();

    /// <summary>
    /// 调用 macOS 接口加载动态库。
    /// </summary>
    [DllImport("/usr/lib/libSystem.dylib", EntryPoint = "dlopen")]
    private static extern IntPtr DlopenMac(string fileName, int flags);
}
