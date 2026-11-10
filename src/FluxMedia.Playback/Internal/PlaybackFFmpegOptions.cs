using System.Diagnostics;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen;
using FluxMedia.Core.Configuration;
using FluxMedia.Core.Logging;
using FluxMedia.Core.Models;

namespace FluxMedia.Playback.Internal;

/// <summary>
/// 生成播放参数并计算网络超时。
/// </summary>
internal static unsafe class PlaybackFFmpegOptions
{
    /// <summary>
    /// 将播放配置转换为 FFmpeg 打开参数。
    /// </summary>
    public static AVDictionary* CreateOpenDictionary(MediaSource source, PlaybackOptions options, Guid instanceId)
    {
        AVDictionary* dictionary = null;
        if (source.Kind != MediaSourceKind.NetworkStream)
        {
            return dictionary;
        }

        var network = options.Network;
        Set(&dictionary, "rw_timeout", ToMicroseconds(EffectiveReadTimeout(options)));
        Set(&dictionary, "timeout", ToMicroseconds(EffectiveOpenTimeout(options)));
        Set(&dictionary, "stimeout", ToMicroseconds(EffectiveOpenTimeout(options)));
        Set(&dictionary, "probesize", Math.Max(4096, network.ProbeSize).ToString(System.Globalization.CultureInfo.InvariantCulture));
        Set(&dictionary, "analyzeduration", ToMicroseconds(network.AnalyzeDuration));
        if (Uri.TryCreate(source.Uri, UriKind.Absolute, out var uri))
        {
            var scheme = uri.Scheme.ToLowerInvariant();
            if (network.ReconnectEnabled && (scheme is "http" or "https" || uri.AbsolutePath.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase)))
            {
                Set(&dictionary, "reconnect", "1");
                Set(&dictionary, "reconnect_streamed", "1");
                Set(&dictionary, "reconnect_on_network_error", "1");
                Set(&dictionary, "reconnect_delay_max", Math.Max(0, network.ReconnectDelayMax).ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            else if (scheme == "rtsp")
            {
                Set(&dictionary, "rtsp_transport", network.RtspTransport == RtspTransportMode.Udp ? "udp" : "tcp");
            }
        }

        FluxMediaLog.Write(LogLevel.Information, "PlaybackFFmpegOptions", instanceId, $"Event=NetworkFFmpegOptions Options={Describe(source, options)}");
        return dictionary;
    }

    /// <summary>
    /// 生成用于诊断的 FFmpeg 参数说明。
    /// </summary>
    public static string Describe(MediaSource source, PlaybackOptions options)
    {
        if (source.Kind != MediaSourceKind.NetworkStream)
        {
            return "local-file-defaults";
        }

        var values = new List<string>
        {
            $"mode={options.Network.Mode}",
            $"low_latency={options.Network.LowLatencyMode}",
            $"rw_timeout={ToMicroseconds(EffectiveReadTimeout(options))}",
            $"timeout={ToMicroseconds(EffectiveOpenTimeout(options))}",
            $"stimeout={ToMicroseconds(EffectiveOpenTimeout(options))}",
            $"probesize={Math.Max(4096,options.Network.ProbeSize)}",
            $"analyzeduration={ToMicroseconds(options.Network.AnalyzeDuration)}"
        };

        if (Uri.TryCreate(source.Uri, UriKind.Absolute, out var uri))
        {
            if (uri.Scheme.Equals("rtsp", StringComparison.OrdinalIgnoreCase))
            {
                values.Add($"rtsp_transport={(options.Network.RtspTransport == RtspTransportMode.Udp ? "udp" : "tcp")}");
            }
            else if (options.Network.ReconnectEnabled && (uri.Scheme is "http" or "https" || uri.AbsolutePath.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase)))
            {
                values.Add("reconnect=1");
                values.Add("reconnect_streamed=1");
                values.Add("reconnect_on_network_error=1");
                values.Add($"reconnect_delay_max={Math.Max(0, options.Network.ReconnectDelayMax)}");
            }
        }

        return string.Join(", ", values);
    }

    /// <summary>
    /// 创建可响应取消和超时的读取中断上下文。
    /// </summary>
    public static PlaybackInterruptContext CreateInterruptContext(TimeSpan timeout, CancellationToken cancellationToken)
    {
        return new PlaybackInterruptContext(timeout, cancellationToken);
    }

    /// <summary>
    /// 计算网络打开实际采用的超时时间。
    /// </summary>
    public static TimeSpan EffectiveOpenTimeout(PlaybackOptions options)
    {
        return MinPositive(options.Network.OpenTimeout, options.NetworkTimeout);
    }

    /// <summary>
    /// 计算网络读取实际采用的超时时间。
    /// </summary>
    public static TimeSpan EffectiveReadTimeout(PlaybackOptions options)
    {
        return MinPositive(options.Network.ReadTimeout, options.NetworkTimeout);
    }

    /// <summary>
    /// 取两个有效正时长中的较小值。
    /// </summary>
    private static TimeSpan MinPositive(TimeSpan first, TimeSpan second)
    {
        if (first <= TimeSpan.Zero)
        {
            return second;
        }

        if (second <= TimeSpan.Zero)
        {
            return first;
        }

        return first <= second ? first : second;
    }

    /// <summary>
    /// 设置 FFmpeg 参数字典中的键值。
    /// </summary>
    private static void Set(AVDictionary** dictionary, string key, string value)
    {
        ffmpeg.av_dict_set(dictionary, key, value, 0);
    }

    /// <summary>
    /// 将时间间隔换算为微秒。
    /// </summary>
    private static string ToMicroseconds(TimeSpan timeout)
    {
        return Math.Max(1, (long)(timeout.TotalMilliseconds * 1000)).ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
}

/// <summary>
/// 通过 FFmpeg 回调中断取消或超时的读取。
/// </summary>
internal sealed unsafe class PlaybackInterruptContext : IDisposable
{
    /// <summary>
    /// 取消令牌。
    /// </summary>
    private readonly CancellationToken _cancellationToken;

    /// <summary>
    /// 传给 FFmpeg 的中断回调。
    /// </summary>
    private readonly AVIOInterruptCB_callback _callback;

    /// <summary>
    /// 句柄。
    /// </summary>
    private GCHandle _handle;

    /// <summary>
    /// 超时。
    /// </summary>
    private TimeSpan _timeout;

    /// <summary>
    /// 当前原生操作的超时截止刻度。
    /// </summary>
    private long _deadlineTicks;

    /// <summary>
    /// 是否已释放资源。
    /// </summary>
    private bool _disposed;

    /// <summary>
    /// 初始化播放中断上下文所需的状态和资源。
    /// </summary>
    public PlaybackInterruptContext(TimeSpan timeout, CancellationToken cancellationToken)
    {
        _timeout = timeout <= TimeSpan.Zero ? TimeSpan.FromSeconds(5) : timeout;
        _cancellationToken = cancellationToken;
        _callback = Interrupt;
        _handle = GCHandle.Alloc(this);
        ResetDeadline();
    }

    /// <summary>
    /// 回调。
    /// </summary>
    public AVIOInterruptCB Callback => new()
    {
        callback = _callback,
        opaque = (void*)GCHandle.ToIntPtr(_handle)
    };

    /// <summary>
    /// 是否已超时输出。
    /// </summary>
    public bool TimedOut => Stopwatch.GetTimestamp() > Interlocked.Read(ref _deadlineTicks);

    /// <summary>
    /// 按当前超时重新计算截止时间。
    /// </summary>
    public void ResetDeadline()
    {
        var ticks = Stopwatch.GetTimestamp() + (long)(_timeout.TotalSeconds * Stopwatch.Frequency);
        Interlocked.Exchange(ref _deadlineTicks, ticks);
    }

    /// <summary>
    /// 更新超时时间并重置截止时间。
    /// </summary>
    public void SetTimeout(TimeSpan timeout)
    {
        _timeout = timeout <= TimeSpan.Zero ? TimeSpan.FromSeconds(5) : timeout;
        ResetDeadline();
    }

    /// <summary>
    /// 释放当前对象持有的资源。
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_handle.IsAllocated)
        {
            _handle.Free();
        }
    }

    /// <summary>
    /// 向 FFmpeg 返回是否应中断当前操作。
    /// </summary>
    private static int Interrupt(void* opaque)
    {
        if (opaque == null)
        {
            return 0;
        }

        var handle = GCHandle.FromIntPtr((nint)opaque);
        if (handle.Target is not PlaybackInterruptContext context)
        {
            return 0;
        }

        return context._cancellationToken.IsCancellationRequested || context.TimedOut ? 1 : 0;
    }
}
