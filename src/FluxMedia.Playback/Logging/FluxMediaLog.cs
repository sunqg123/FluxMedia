using FluxMedia.Core.Configuration;

namespace FluxMedia.Core.Logging;

/// <summary>
/// 全局静态日志入口，默认关闭，无需向播放器或组件传入日志器。
/// </summary>
public static class FluxMediaLog
{
    /// <summary>
    /// 串行修改日志配置，不在锁内执行宿主回调。
    /// </summary>
    private static readonly object ConfigurationGate = new();

    /// <summary>
    /// 当前输出目标，持有一个配置所有权引用。
    /// </summary>
    private static LogTarget _target = new(null);

    /// <summary>
    /// 下次启用默认文件输出时采用的配置。
    /// </summary>
    private static LoggingOptions _fileOptions = new();

    /// <summary>
    /// 日志总开关，关闭时不访问输出目标。
    /// </summary>
    private static int _enabled;

    /// <summary>
    /// 最低输出级别，允许在运行中调整。
    /// </summary>
    private static int _minimumLevel = (int)LogLevel.Information;

    /// <summary>
    /// 配置版本，防止旧运行时关闭新配置。
    /// </summary>
    private static long _generation;

    /// <summary>
    /// 阻止宿主回调再次写日志引发递归。
    /// </summary>
    [ThreadStatic]
    private static int _dispatchDepth;

    /// <summary>
    /// 日志总开关；首次打开且未设置输出目标时创建默认文件输出。
    /// </summary>
    public static bool IsEnabled
    {
        get => Volatile.Read(ref _enabled) != 0;
        set
        {
            LogTarget? previous = null;
            lock (ConfigurationGate)
            {
                if (value && MinimumLevel != LogLevel.None)
                {
                    previous = EnsureTargetLocked();
                }
                Volatile.Write(ref _enabled, value ? 1 : 0);
            }
            previous?.Release();
        }
    }

    /// <summary>
    /// 最低输出级别，None 表示不输出任何日志。
    /// </summary>
    public static LogLevel MinimumLevel
    {
        get => (LogLevel)Volatile.Read(ref _minimumLevel);
        set
        {
            if (value < LogLevel.Trace || value > LogLevel.None)
            {
                throw new ArgumentOutOfRangeException(nameof(value));
            }
            LogTarget? previous = null;
            lock (ConfigurationGate)
            {
                if (IsEnabled && value != LogLevel.None)
                    previous = EnsureTargetLocked();
                Volatile.Write(ref _minimumLevel, (int)value);
            }
            previous?.Release();
        }
    }

    /// <summary>
    /// 当前文件日志的完整目录，其他输出方式为空。
    /// </summary>
    public static string? LogDirectory => (Volatile.Read(ref _target).Sink as FileFluxMediaLogSink)?.LogDirectory;

    /// <summary>
    /// 当前文件日志路径，未配置文件输出时为空。
    /// </summary>
    public static string? CurrentLogFilePath => (Volatile.Read(ref _target).Sink as FileFluxMediaLogSink)?.CurrentLogFilePath;

    /// <summary>
    /// 配置异步文件日志；关闭状态不创建目录或后台写入任务。
    /// </summary>
    public static void Configure(LoggingOptions options) => ConfigureCore(options);

    /// <summary>
    /// 按指定选项启用文件输出，省略参数时使用默认配置。
    /// </summary>
    public static void UseFile(LoggingOptions? options = null) => Configure(options ?? new LoggingOptions());

    /// <summary>
    /// 切换为控制台输出，开关与最低级别保持不变。
    /// </summary>
    public static void UseConsole() => ReplaceTarget(new ConsoleFluxMediaLogSink(LogLevel.Trace));

    /// <summary>
    /// 使用宿主委托接管输出，不需要实现日志接口或注入到播放器。
    /// </summary>
    public static void SetSink(Action<LogLevel, string, Guid, string, Exception?> writer)
    {
        if (writer is null) throw new ArgumentNullException(nameof(writer));
        ReplaceTarget(new DelegateLogSink(writer));
    }

    /// <summary>
    /// 使用不关心会话标识的宿主委托接管输出。
    /// </summary>
    public static void SetSink(Action<LogLevel, string, string, Exception?> writer)
    {
        if (writer is null) throw new ArgumentNullException(nameof(writer));
        SetSink((level, category, _, message, error) => writer(level, category, message, error));
    }

    /// <summary>
    /// 判断级别是否启用，可用于避免构造昂贵的诊断文本。
    /// </summary>
    public static bool IsEnabledFor(LogLevel level) => IsEnabled && level >= MinimumLevel && level < LogLevel.None;

    /// <summary>
    /// 输出跟踪日志。
    /// </summary>
    public static void Trace(string category, string message, Guid instanceId = default) => Write(LogLevel.Trace, category, instanceId, message);

    /// <summary>
    /// 输出调试日志。
    /// </summary>
    public static void Debug(string category, string message, Guid instanceId = default) => Write(LogLevel.Debug, category, instanceId, message);

    /// <summary>
    /// 输出信息日志。
    /// </summary>
    public static void Info(string category, string message, Guid instanceId = default) => Write(LogLevel.Information, category, instanceId, message);

    /// <summary>
    /// 输出警告日志。
    /// </summary>
    public static void Warning(string category, string message, Guid instanceId = default) => Write(LogLevel.Warning, category, instanceId, message);

    /// <summary>
    /// 输出错误日志及可选异常。
    /// </summary>
    public static void Error(string category, string message, Exception? exception = null, Guid instanceId = default) => Write(LogLevel.Error, category, instanceId, message, exception);

    /// <summary>
    /// 输出严重错误及可选异常。
    /// </summary>
    public static void Critical(string category, string message, Exception? exception = null, Guid instanceId = default) => Write(LogLevel.Critical, category, instanceId, message, exception);

    /// <summary>
    /// 输出普通日志，未提供会话标识时使用空标识。
    /// </summary>
    public static void Write(LogLevel level, string category, string message, Exception? exception = null) => Write(level, category, Guid.Empty, message, exception);

    /// <summary>
    /// 输出带会话标识的日志，输出异常不会传播到播放流程。
    /// </summary>
    public static void Write(LogLevel level, string module, Guid instanceId, string message, Exception? exception = null)
    {
        if (!IsEnabledFor(level) || _dispatchDepth != 0) return;
        var target = AcquireTarget();
        if (target is null) return;
        _dispatchDepth++;
        try
        {
            target.Sink!.Write(level, module, instanceId, message, exception);
        }
        catch
        {
            /* 日志失败不改变播放结果。 */
        }
        finally
        {
            _dispatchDepth--;
            target.Release();
        }
    }

    /// <summary>
    /// 刷新当前输出目标，不受总开关和级别过滤影响。
    /// </summary>
    public static void Flush()
    {
        if (_dispatchDepth != 0) return;
        var target = AcquireTarget();
        if (target is null) return;
        _dispatchDepth++;
        try
        {
            target.Sink!.Flush();
        }
        catch
        {
            /* 刷新失败不影响主流程。 */
        }
        finally
        {
            _dispatchDepth--;
            target.Release();
        }
    }

    /// <summary>
    /// 关闭日志并释放当前输出，在途写入结束后释放其持有的旧目标。
    /// </summary>
    public static void Shutdown() => ShutdownCore(null);

    /// <summary>
    /// 为引擎创建文件输出，返回所有权版本。
    /// </summary>
    internal static long ConfigureForEngine(LoggingOptions options) => ConfigureCore(options);

    /// <summary>
    /// 仅关闭指定版本，避免引擎清理宿主新设置的输出。
    /// </summary>
    internal static void ShutdownIfCurrent(long generation) => ShutdownCore(generation);

    /// <summary>
    /// 创建配置并原子切换输出目标。
    /// </summary>
    private static long ConfigureCore(LoggingOptions options)
    {
        if (options is null) throw new ArgumentNullException(nameof(options));
        if (options.MinimumLevel < LogLevel.Trace || options.MinimumLevel > LogLevel.None)
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }
        var next = new LogTarget(options.IsEnabled && options.MinimumLevel != LogLevel.None
            ? new FileFluxMediaLogSink(options with { MinimumLevel = LogLevel.Trace }) : null);
        LogTarget previous;
        long generation;
        lock (ConfigurationGate)
        {
            _fileOptions = options;
            previous = Interlocked.Exchange(ref _target, next);
            Volatile.Write(ref _minimumLevel, (int)options.MinimumLevel);
            Volatile.Write(ref _enabled, options.IsEnabled ? 1 : 0);
            generation = ++_generation;
        }
        previous.Release();
        return generation;
    }

    /// <summary>
    /// 切换输出并退还旧目标的配置引用，不在配置锁内清理资源。
    /// </summary>
    private static void ReplaceTarget(IFluxMediaLogSink sink)
    {
        LogTarget previous;
        lock (ConfigurationGate)
        {
            previous = Interlocked.Exchange(ref _target, new LogTarget(sink));
            _generation++;
        }
        previous.Release();
    }

    /// <summary>
    /// 首次启用时创建默认文件目标，调用方必须持有配置锁。
    /// </summary>
    private static LogTarget? EnsureTargetLocked()
    {
        if (_target.Sink is not null) return null;
        var previous = Interlocked.Exchange(ref _target,
            new LogTarget(new FileFluxMediaLogSink(_fileOptions with { IsEnabled = true, MinimumLevel = LogLevel.Trace })));
        _generation++;
        return previous;
    }

    /// <summary>
    /// 保留当前目标，防止切换或关闭时提前释放仍在写入的资源。
    /// </summary>
    private static LogTarget? AcquireTarget()
    {
        while (true)
        {
            var target = Volatile.Read(ref _target);
            if (target.Sink is null) return null;
            if (target.TryRetain()) return target;
        }
    }

    /// <summary>
    /// 清理当前配置，指定版本已过期时保持新配置不变。
    /// </summary>
    private static void ShutdownCore(long? expectedGeneration)
    {
        LogTarget previous;
        lock (ConfigurationGate)
        {
            if (expectedGeneration.HasValue && _generation != expectedGeneration.Value) return;
            Volatile.Write(ref _enabled, 0);
            previous = Interlocked.Exchange(ref _target, new LogTarget(null));
            _fileOptions = new LoggingOptions();
            Volatile.Write(ref _minimumLevel, (int)LogLevel.Information);
            _generation++;
        }
        previous.Release();
    }
}
