using FluxMedia.Core.Configuration;
using FluxMedia.Core.Errors;
using FluxMedia.Core.Logging;
using FluxMedia.Core.Models;
using FluxMedia.Playback.Internal;

namespace FluxMedia.Playback;

/// <summary>
/// 串行执行播放命令，公开实际状态和播放事件。
/// </summary>
public sealed partial class Player : IAsyncDisposable
{
    /// <summary>
    /// 底层播放会话。
    /// </summary>
    private readonly PlaybackSession _session;

    /// <summary>
    /// 串行执行播放器命令的锁。
    /// </summary>
    private readonly SemaphoreSlim _commandGate = new(1, 1);

    /// <summary>
    /// 保护单次资源释放的锁。
    /// </summary>
    private readonly object _disposeSync = new();

    /// <summary>
    /// 确保多次释放共享同一个完成任务。
    /// </summary>
    private Task? _disposeTask;

    /// <summary>
    /// 是否已释放资源。
    /// </summary>
    private volatile bool _disposed;

    /// <summary>
    /// 绑定一个由引擎创建的独立播放会话。
    /// </summary>
    internal Player(PlaybackSession session)
    {
        _session = session;
        _session.StateChanged += OnSessionStateChanged;
        _session.Faulted += OnSessionFaulted;
        _session.PositionChanged += OnSessionPositionChanged;
        _session.PlaybackRateChanged += OnSessionPlaybackRateChanged;
        // 视频链路⑤订阅：接收会话的帧事件。
        _session.VideoFrameReady += OnSessionVideoFrameReady;
    }

    /// <summary>
    /// 当前播放器底层会话的唯一标识。
    /// </summary>
    public Guid InstanceId => _session.InstanceId;

    /// <summary>
    /// 底层会话当前的播放状态。
    /// </summary>
    public PlaybackState State => _session.State;

    /// <summary>
    /// 当前是否处于播放状态。
    /// </summary>
    public bool IsPlaying => State == PlaybackState.Playing;

    /// <summary>
    /// 播放器或底层会话是否已释放。
    /// </summary>
    public bool IsDisposed => _disposed || _session.IsDisposed;

    /// <summary>
    /// 当前媒体源。
    /// </summary>
    public MediaSource? Source => _session.CurrentSource;

    /// <summary>
    /// 核心报告的实际播放位置，跳转通过 SeekAsync 提交。
    /// </summary>
    public TimeSpan Position => _session.CurrentTime;

    /// <summary>
    /// 媒体总时长，未知时为空。
    /// </summary>
    public TimeSpan? Duration => _session.Duration;

    /// <summary>
    /// 核心确认当前源属于直播。
    /// </summary>
    public bool IsLive => _session.IsLive;

    /// <summary>
    /// 当前媒体是否允许跳转。
    /// </summary>
    public bool CanSeek => !_disposed && _session.CanSeek;

    /// <summary>
    /// 核心是否正在执行跳转。
    /// </summary>
    public bool IsSeeking => State == PlaybackState.Seeking;

    /// <summary>
    /// 当前媒体是否允许调整倍速。
    /// </summary>
    public bool CanChangeSpeed => !_disposed && _session.CanChangeSpeed;

    /// <summary>
    /// 最新请求的目标倍速，可能尚未应用。
    /// </summary>
    public double RequestedSpeed => _session.RequestedPlaybackRate;

    /// <summary>
    /// 是否仍有倍速等待实际应用。
    /// </summary>
    public bool IsSpeedChangePending => !_disposed && _session.IsPlaybackRatePending;

    /// <summary>
    /// 最近换速失败原因，不等同于播放器故障。
    /// </summary>
    public string? LastSpeedError => _session.LastPlaybackRateError;

    /// <summary>
    /// 已确认应用的实际倍速。
    /// </summary>
    public double Speed => _session.PlaybackRate;

    /// <summary>
    /// 核心已采用的音量，范围为 0 到 1。
    /// </summary>
    public double Volume => _session.Volume;

    /// <summary>
    /// 核心已采用的静音状态。
    /// </summary>
    public bool IsMuted => _session.IsMuted;

    /// <summary>
    /// 当前录制的运行状态。
    /// </summary>
    public PlaybackRecordingState RecordingState => _session.RecordingState;

    /// <summary>
    /// 录制是否正在启动或进行中。
    /// </summary>
    public bool IsRecording => RecordingState is PlaybackRecordingState.Starting or PlaybackRecordingState.Recording;

    /// <summary>
    /// 组件运行状态变化时发出的通知。
    /// </summary>
    public event EventHandler<PlaybackState>? StateChanged;

    /// <summary>
    /// 发生运行错误时发出的通知。
    /// </summary>
    public event EventHandler<FluxMediaError>? Faulted;

    /// <summary>
    /// 播放位置变化时发出的通知。
    /// </summary>
    public event EventHandler<PlaybackPositionChangedEventArgs>? PositionChanged;

    /// <summary>
    /// 新视频帧可供显示时发出的通知。
    /// </summary>
    public event EventHandler<PlaybackVideoFrameEventArgs>? VideoFrameReady;

    /// <summary>
    /// 媒体、控制值或请求状态变化；在触发线程通知，订阅者负责线程切换。
    /// </summary>
    public event EventHandler<EventArgs>? PlaybackChanged;

    /// <summary>
    /// 更新显示端支持的 GPU 输出能力。
    /// </summary>
    public void ConfigureVideoOutput(VideoOutputCapabilities capabilities)
    {
        if (capabilities is null)
        {
            throw new ArgumentNullException(nameof(capabilities));
        }
        _session.ConfigureVideoOutput(capabilities);
    }

    /// <summary>
    /// 打开本地路径或网络地址，但不自动开始播放。
    /// </summary>
    public async ValueTask OpenAsync(string pathOrUrl, PlaybackOptions? options = null, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var source = MediaSourcePolicy.Parse(pathOrUrl);
        await OpenAsync(source, options, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 打开媒体源并读取媒体信息。
    /// </summary>
    public ValueTask OpenAsync(MediaSource source, PlaybackOptions? options = null, CancellationToken cancellationToken = default) => ExecuteCommandAsync(async token =>
        {
            await _session.OpenAsync(new PlaybackOpenRequest(source, options), token).ConfigureAwait(false);
            NotifyPlaybackChanged();
        }, cancellationToken);

    /// <summary>
    /// 打开本地路径或网络地址并立即开始播放。
    /// </summary>
    public async ValueTask<PlaybackResult> OpenAndPlayAsync(string pathOrUrl, PlaybackOptions? options = null, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var source = MediaSourcePolicy.Parse(pathOrUrl);
        return await OpenAndPlayAsync(source, options, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 打开媒体源并开始播放。
    /// </summary>
    public ValueTask<PlaybackResult> OpenAndPlayAsync(MediaSource source, PlaybackOptions? options = null, CancellationToken cancellationToken = default) => ExecuteCommandAsync<PlaybackResult>(async token =>
        {
            await _session.OpenAsync(new PlaybackOpenRequest(source, options), token).ConfigureAwait(false);
            NotifyPlaybackChanged();
            // 打开期间或通知回调中可能开始释放，不再继续启动播放。
            ThrowIfDisposed();
            return await _session.PlayAsync(token).ConfigureAwait(false);
        }, cancellationToken);

    /// <summary>
    /// 打开网络媒体源。
    /// </summary>
    public async ValueTask OpenUrlAsync(string url, PlaybackOptions? options = null, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await OpenAsync(MediaSource.FromNetwork(url), options, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 开始或重播当前媒体。
    /// </summary>
    public ValueTask<PlaybackResult> PlayAsync(CancellationToken cancellationToken = default) => ExecuteCommandAsync(_session.PlayAsync, cancellationToken);

    /// <summary>
    /// 暂停播放。
    /// </summary>
    public ValueTask<PlaybackResult> PauseAsync(CancellationToken cancellationToken = default) => ExecuteCommandAsync(_session.PauseAsync, cancellationToken);

    /// <summary>
    /// 恢复暂停的播放。
    /// </summary>
    public ValueTask<PlaybackResult> ResumeAsync(CancellationToken cancellationToken = default) => ExecuteCommandAsync(_session.ResumeAsync, cancellationToken);

    /// <summary>
    /// 按执行时的状态切换播放或暂停。
    /// </summary>
    public ValueTask<PlaybackResult> TogglePlayPauseAsync(CancellationToken cancellationToken = default) => ExecuteCommandAsync(token => IsPlaying ? _session.PauseAsync(token) : _session.PlayAsync(token), cancellationToken);

    /// <summary>
    /// 提交跳转，由会话调度器合并连续目标。
    /// </summary>
    public ValueTask<PlaybackResult> SeekAsync(TimeSpan position, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return _session.SeekAsync(position, cancellationToken);
    }

    /// <summary>
    /// 相对当前位置提交跳转。
    /// </summary>
    public ValueTask<PlaybackResult> SeekByAsync(TimeSpan offset, CancellationToken cancellationToken = default) => SeekAsync(_session.CurrentTime + offset, cancellationToken);

    /// <summary>
    /// 设置速度并等待核心确认。
    /// </summary>
    public async ValueTask<PlaybackResult> SetSpeedAsync(double speed, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var result = await _session.SetPlaybackRateAsync(speed, cancellationToken).ConfigureAwait(false);
        OnSessionPlaybackRateChanged();
        return result;
    }

    /// <summary>
    /// 设置音量并返回核心结果。
    /// </summary>
    public ValueTask<PlaybackResult> SetVolumeAsync(double volume, CancellationToken cancellationToken = default) => ExecuteCommandAsync<PlaybackResult>(async token =>
        {
            var result = await _session.SetVolumeAsync(volume, token).ConfigureAwait(false);
            NotifyPlaybackChanged();
            return result;
        }, cancellationToken);

    /// <summary>
    /// 设置静音并返回核心结果。
    /// </summary>
    public ValueTask<PlaybackResult> SetMutedAsync(bool muted, CancellationToken cancellationToken = default) => ExecuteCommandAsync<PlaybackResult>(async token =>
        {
            var result = await _session.SetMutedAsync(muted, token).ConfigureAwait(false);
            NotifyPlaybackChanged();
            return result;
        }, cancellationToken);

    /// <summary>
    /// 按实际设备状态切换静音。
    /// </summary>
    public ValueTask<PlaybackResult> ToggleMutedAsync(CancellationToken cancellationToken = default) => ExecuteCommandAsync<PlaybackResult>(async token =>
        {
            var result = await _session.SetMutedAsync(!_session.IsMuted, token).ConfigureAwait(false);
            NotifyPlaybackChanged();
            return result;
        }, cancellationToken);

    /// <summary>
    /// 将当前播放数据包封装为文件，不重新编码。
    /// </summary>
    public ValueTask<PlaybackResult> StartRecordingAsync(string outputPath, CancellationToken cancellationToken = default)
    {
        return StartRecordingAsync(new PlaybackRecordingOptions { OutputPath = outputPath }, cancellationToken);
    }

    /// <summary>
    /// 开始录制当前播放的数据包。
    /// </summary>
    public ValueTask<PlaybackResult> StartRecordingAsync(PlaybackRecordingOptions options, CancellationToken cancellationToken = default) => ExecuteCommandAsync<PlaybackResult>(async token =>
        {
            var result = await _session.StartRecordingAsync(options, token).ConfigureAwait(false);
            NotifyPlaybackChanged();
            return result;
        }, cancellationToken);

    /// <summary>
    /// 停止并封装当前录制文件。
    /// </summary>
    public ValueTask<PlaybackResult> StopRecordingAsync(CancellationToken cancellationToken = default) => ExecuteCommandAsync<PlaybackResult>(async token =>
        {
            var result = await _session.StopRecordingAsync(token).ConfigureAwait(false);
            NotifyPlaybackChanged();
            return result;
        }, cancellationToken);

    /// <summary>
    /// 读取当前媒体信息。
    /// </summary>
    public ValueTask<MediaInfo> GetMediaInfoAsync(CancellationToken cancellationToken = default) => ExecuteCommandAsync(_session.GetMediaInfoAsync, cancellationToken);

    /// <summary>
    /// 依次读取视频帧；跨迭代使用时需保留租约。
    /// </summary>
    public IAsyncEnumerable<VideoFrame> ReadVideoFramesAsync(CancellationToken cancellationToken = default)
    {
        return _session.ReadVideoFramesAsync(cancellationToken);
    }

    /// <summary>
    /// 先取消正在打开或跳转的动作，再等待命令锁执行停止。
    /// </summary>
    public async ValueTask StopAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        _session.CancelPendingOperations();
        await ExecuteCommandAsync(_session.StopAsync, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 释放当前对象持有的资源。
    /// </summary>
    public ValueTask DisposeAsync()
    {
        TaskCompletionSource<bool> completion;
        lock (_disposeSync)
        {
            if (_disposeTask is not null)
                return new ValueTask(_disposeTask);
            completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _disposeTask = completion.Task;
            _disposed = true;
        }
        // 先登记共享任务，再通知订阅者；重入只会取得同一任务。
        _ = DisposeAndCompleteAsync(completion);
        return new ValueTask(completion.Task);
    }

    /// <summary>
    /// 将清理结果传给所有释放调用方，保留异常与取消状态。
    /// </summary>
    private async Task DisposeAndCompleteAsync(TaskCompletionSource<bool> completion)
    {
        try
        {
            await DisposeCoreAsync().ConfigureAwait(false);
            completion.TrySetResult(true);
        }
        catch (OperationCanceledException exception) { completion.TrySetCanceled(exception.CancellationToken); }
        catch (Exception exception) { completion.TrySetException(exception); }
    }

    /// <summary>
    /// 取消待处理操作，并等待命令结束后释放会话。
    /// </summary>
    private async Task DisposeCoreAsync()
    {
        NotifyPlaybackChanged();
        _session.CancelPendingOperations();
        try
        {
            await _commandGate.WaitAsync().ConfigureAwait(false);
            try
            {
                _session.StateChanged -= OnSessionStateChanged;
                _session.Faulted -= OnSessionFaulted;
                _session.PositionChanged -= OnSessionPositionChanged;
                _session.PlaybackRateChanged -= OnSessionPlaybackRateChanged;
                _session.VideoFrameReady -= OnSessionVideoFrameReady;
                await _session.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                _commandGate.Release();
            }
        }
        finally
        {
            FluxEngine.Unregister(this);

            _commandGate.Dispose();
            NotifyPlaybackChanged();
        }
    }

    /// <summary>
    /// 转发换速请求与确认变化，不进行 UI 线程派发。
    /// </summary>
    private void OnSessionPlaybackRateChanged() => NotifyPlaybackChanged();

    /// <summary>
    /// 转发核心状态并通知可读数据变化。
    /// </summary>
    private void OnSessionStateChanged(object? sender, PlaybackState state)
    {
        NotifySubscribers(StateChanged, state, nameof(StateChanged));
        NotifyPlaybackChanged();
    }

    /// <summary>
    /// 转发底层会话的错误通知。
    /// </summary>
    private void OnSessionFaulted(object? sender, FluxMediaError error)
    {
        NotifySubscribers(Faulted, error, nameof(Faulted));
        NotifyPlaybackChanged();
    }

    /// <summary>
    /// 将底层视频帧通知转发给显示端。
    /// </summary>
    // 视频链路⑤转发：触发公开帧事件，交给显示端。
    private void OnSessionVideoFrameReady(object? sender, PlaybackVideoFrameEventArgs args) => NotifySubscribers(VideoFrameReady, args, nameof(VideoFrameReady));

    /// <summary>
    /// 在核心触发线程转发媒体进度。
    /// </summary>
    private void OnSessionPositionChanged(object? sender, PlaybackPositionChangedEventArgs args) =>
        NotifySubscribers(PositionChanged, args, nameof(PositionChanged));

    /// <summary>
    /// 通知实际状态变化，释放开始时也通知观察者。
    /// </summary>
    private void NotifyPlaybackChanged() =>
        NotifySubscribers(PlaybackChanged, EventArgs.Empty, nameof(PlaybackChanged), allowDisposed: true);

    /// <summary>
    /// 隔离每个订阅者的异常，避免影响其它显示端或通知。
    /// </summary>
    private void NotifySubscribers<TEvent>(EventHandler<TEvent>? handlers, TEvent value, string eventName, bool allowDisposed = false)
    {
        if (handlers is null || _disposed && !allowDisposed)
            return;
        foreach (EventHandler<TEvent> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, value);
            }
            catch (Exception exception)
            {
                FluxMediaLog.Write(LogLevel.Warning, nameof(Player), InstanceId, $"Event=SubscriberFailed Notification={eventName}", exception);
            }
        }
    }

    /// <summary>
    /// 序列化普通命令；跳转请求由专用调度器负责合并。
    /// </summary>
    private async ValueTask<TResult> ExecuteCommandAsync<TResult>(Func<CancellationToken, ValueTask<TResult>> command, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await _commandGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            return await command(cancellationToken).ConfigureAwait(false);
        }
        finally { _commandGate.Release(); }
    }
    /// <summary>
    /// 序列化不返回结果的命令。
    /// </summary>
    private async ValueTask ExecuteCommandAsync(Func<CancellationToken, ValueTask> command, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await _commandGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            await command(cancellationToken).ConfigureAwait(false);
        }
        finally { _commandGate.Release(); }
    }
    /// <summary>
    /// 释放开始后拒绝新命令。
    /// </summary>
    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(Player));
    }
}
