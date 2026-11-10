using FluxMedia.Core.Configuration;
using System.Collections.Concurrent;
using System.Text;

namespace FluxMedia.Core.Logging;

/// <summary>
/// 异步文件日志输出，按配置滚动文件。
/// </summary>
internal sealed class FileFluxMediaLogSink : IFluxMediaLogSink, IDisposable
{
    /// <summary>
    /// 等待后台写入的日志记录队列。
    /// </summary>
    private readonly BlockingCollection<LogRecord> _queue;

    /// <summary>
    /// 释放令牌取消源。
    /// </summary>
    private readonly CancellationTokenSource _disposeToken = new();

    /// <summary>
    /// 持续消费并写入日志记录的后台任务。
    /// </summary>
    private readonly Task _writerTask;

    /// <summary>
    /// 日志目录。
    /// </summary>
    private readonly string _logDirectory;

    /// <summary>
    /// 文件前缀。
    /// </summary>
    private readonly string _filePrefix;

    /// <summary>
    /// 滚动模式。
    /// </summary>
    private readonly LogFileRollingMode _rollingMode;

    /// <summary>
    /// 刷新间隔。
    /// </summary>
    private readonly TimeSpan _flushInterval;

    /// <summary>
    /// 区分不同运行实例的时间标记。
    /// </summary>
    private readonly string _runStamp = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss");

    /// <summary>
    /// 日志文件使用的 UTF-8 编码。
    /// </summary>
    private readonly Encoding _encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);

    /// <summary>
    /// 串行处理主动日志刷新的锁。
    /// </summary>
    private readonly object _flushGate = new();

    /// <summary>
    /// 已请求停止写入，使用整数保证只执行一次释放。
    /// </summary>
    private int _disposeStarted;

    /// <summary>
    /// 队列和取消源是否已完成清理。
    /// </summary>
    private int _resourcesDisposed;

    /// <summary>
    /// 当前实际写入的文件路径，跨天滚动时更新。
    /// </summary>
    private string _currentLogFilePath;

    /// <summary>
    /// 是否已释放资源。
    /// </summary>
    private volatile bool _disposed;

    /// <summary>
    /// 创建文件输出并启动后台写入任务。
    /// </summary>
    public FileFluxMediaLogSink(LoggingOptions? options = null)
    {
        var actualOptions = options ?? new LoggingOptions();
        IsEnabled = actualOptions.IsEnabled;
        MinimumLevel = actualOptions.MinimumLevel;
        _logDirectory = ResolveLogDirectory(actualOptions.Directory);
        _filePrefix = actualOptions.FilePrefix?.Trim() ?? string.Empty;
        _rollingMode = actualOptions.RollingMode;
        _flushInterval = actualOptions.FlushInterval > TimeSpan.Zero ? actualOptions.FlushInterval : TimeSpan.FromSeconds(1);
        _queue = new BlockingCollection<LogRecord>(Math.Max(128, actualOptions.QueueCapacity));
        Directory.CreateDirectory(_logDirectory);
        _currentLogFilePath = BuildLogFilePath(DateTimeOffset.Now);
        _writerTask = Task.Run(WriteLoopAsync);
    }

    /// <summary>
    /// 是否启用。
    /// </summary>
    public bool IsEnabled { get; }

    /// <summary>
    /// 允许输出的最低日志级别。
    /// </summary>
    public LogLevel MinimumLevel { get; }

    /// <summary>
    /// 当前日志文件路径。
    /// </summary>
    public string CurrentLogFilePath => Volatile.Read(ref _currentLogFilePath);

    /// <summary>
    /// 文件日志使用的完整目录。
    /// </summary>
    public string LogDirectory => _logDirectory;

    /// <summary>
    /// 将日志加入后台队列，过载时丢弃新记录。
    /// </summary>
    public void Write(LogLevel level, string module, Guid instanceId, string message, Exception? exception = null)
    {
        if (!IsEnabled || _disposed || level < MinimumLevel)
        {
            return;
        }

        var record = new LogRecord(
            DateTimeOffset.Now,
            level,
            module,
            instanceId,
            Environment.CurrentManagedThreadId,
            message,
            exception,
            null);

        try
        {
            // 满载时丢弃新日志，避免挤掉已经入队的刷新屏障。
            _queue.TryAdd(record);
        }
        catch (InvalidOperationException) { }
    }

    /// <summary>
    /// 发送刷新信号并限时等待后台写入。
    /// </summary>
    public void Flush()
    {
        if (_disposed)
        {
            return;
        }

        lock (_flushGate)
        {
            var signal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            try
            {
                if (_disposed || !_queue.TryAdd(new LogRecord(
                DateTimeOffset.Now,
                MinimumLevel,
                nameof(FileFluxMediaLogSink),
                Guid.Empty,
                Environment.CurrentManagedThreadId,
                string.Empty,
                null,
                    signal))) return;
                signal.Task.Wait(TimeSpan.FromSeconds(3));
            }
            catch (InvalidOperationException) { }
        }
    }

    /// <summary>
    /// 释放当前对象持有的资源。
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
        {
            return;
        }

        _disposed = true;
        _queue.CompleteAdding();
        _disposeToken.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            if (_writerTask.Wait(TimeSpan.FromSeconds(3)))
            {
                DisposeCompletedResources();
                return;
            }
        }
        catch
        {
            // 日志释放不能影响业务退出。
        }
        // 即使磁盘阻塞，也等后台线程真正退出后再释放它仍在使用的队列。
        _ = _writerTask.ContinueWith(_ => DisposeCompletedResources(), CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    /// <summary>
    /// 后台写入退出后清理队列与取消源。
    /// </summary>
    private void DisposeCompletedResources()
    {
        if (Interlocked.Exchange(ref _resourcesDisposed, 1) != 0) return;
        _disposeToken.Dispose();
        _queue.Dispose();
    }

    /// <summary>
    /// 在后台写入日志并按配置刷新文件。
    /// </summary>
    private async Task WriteLoopAsync()
    {
        FileStream? activeStream = null;
        StreamWriter? activeWriter = null;
        string? activePath = null;
        try
        {
            var flushWaitMilliseconds = Math.Max(1, (int)Math.Min(int.MaxValue, _flushInterval.TotalMilliseconds));
            while (!_queue.IsCompleted)
            {
                if (!_queue.TryTake(out var record, flushWaitMilliseconds, _disposeToken.Token))
                {
                    if (activeWriter is not null)
                    {
                        await activeWriter.FlushAsync().ConfigureAwait(false);
                    }
                    continue;
                }

                if (record.FlushSignal is not null)
                {
                    if (activeWriter is not null)
                    {
                        await activeWriter.FlushAsync().ConfigureAwait(false);
                        activeWriter.Dispose();
                        activeStream?.Dispose();
                        activeWriter = null;
                        activeStream = null;
                        activePath = null;
                    }
                    record.FlushSignal.TrySetResult(true);
                    continue;
                }

                var filePath = BuildLogFilePath(record.Timestamp);
                if (!string.Equals(activePath, filePath, StringComparison.OrdinalIgnoreCase))
                {
                    if (activeWriter is not null)
                    {
                        await activeWriter.FlushAsync().ConfigureAwait(false);
                        activeWriter.Dispose();
                        activeStream?.Dispose();
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
                    activeStream = new FileStream(filePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite, 64 * 1024, useAsync: true);
                    activeWriter = new StreamWriter(activeStream, _encoding, 64 * 1024, leaveOpen: true);
                    activePath = filePath;
                    Volatile.Write(ref _currentLogFilePath, filePath);
                }

                await activeWriter!.WriteAsync(FormatRecord(record)).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
            // 文件故障只停止当前输出，不影响播放线程。
            _queue.CompleteAdding();
        }
        finally
        {
            if (activeWriter is not null)
            {
                try
                {
                    await activeWriter.FlushAsync().ConfigureAwait(false);
                }
                catch
                {
                }
                activeWriter.Dispose();
            }
            activeStream?.Dispose();
            while (_queue.TryTake(out var pending))
                pending.FlushSignal?.TrySetResult(false);
        }
    }

    /// <summary>
    /// 根据日期和滚动策略生成日志路径。
    /// </summary>
    private string BuildLogFilePath(DateTimeOffset timestamp)
    {
        var fileName = _rollingMode == LogFileRollingMode.Daily
            ? string.IsNullOrWhiteSpace(_filePrefix) ? $"{timestamp:yyyyMMdd}.txt" : $"{_filePrefix}-{timestamp:yyyyMMdd}.txt"
            : string.IsNullOrWhiteSpace(_filePrefix) ? $"{_runStamp}.txt" : $"{_filePrefix}-{_runStamp}.txt";
        return Path.Combine(_logDirectory, fileName);
    }

    /// <summary>
    /// 将日志目录配置转换为实际路径。
    /// </summary>
    private static string ResolveLogDirectory(string directory)
    {
        var actualDirectory = string.IsNullOrWhiteSpace(directory) ? "logs" : directory.Trim();
        return Path.IsPathRooted(actualDirectory) ? actualDirectory : Path.Combine(AppContext.BaseDirectory, actualDirectory);
    }

    /// <summary>
    /// 将日志记录转换为可写入的文本。
    /// </summary>
    private static string FormatRecord(LogRecord record)
    {
        return FluxMediaLogFormatter.FormatLine(record.Timestamp, record.Level, record.Module, record.InstanceId, record.ThreadId, record.Message, record.Exception);
    }

    /// <summary>
    /// 待写入文件的日志记录。
    /// </summary>
    /// <param name="Timestamp">当前帧或事件的时间戳。</param>
    /// <param name="Level">日志的严重级别。</param>
    /// <param name="Module">生成日志或错误的模块名称。</param>
    /// <param name="InstanceId">当前组件的实例标识。</param>
    /// <param name="ThreadId">生成日志的线程标识。</param>
    /// <param name="Message">当前事件或操作的说明。</param>
    /// <param name="Exception">产生当前错误的原始异常。</param>
    /// <param name="FlushSignal">通知日志刷新完成的信号。</param>
    private sealed record LogRecord(
        DateTimeOffset Timestamp,
        LogLevel Level,
        string Module,
        Guid InstanceId,
        int ThreadId,
        string Message,
        Exception? Exception,
        TaskCompletionSource<bool>? FlushSignal);
}


