using FluxMedia.Core.Errors;
using FluxMedia.Core.Models;
using FluxMedia.Core.Logging;
using FluxMedia.Core.Configuration;
using System.Collections.Concurrent;
using System.ComponentModel;

namespace FluxMedia.Playback;

/// <summary>
/// 串行执行播放命令，并提供界面绑定属性。
/// </summary>
public sealed class Player : IAsyncDisposable, INotifyPropertyChanged
{
    /// <summary>
    /// 按属性名复用的变更事件参数。
    /// </summary>
    private static readonly ConcurrentDictionary<string, PropertyChangedEventArgs> PropertyChangedArgs = new(StringComparer.Ordinal);

    /// <summary>
    /// 底层播放会话。
    /// </summary>
    private readonly IPlaybackSession _session;

    /// <summary>
    /// 串行执行播放器命令的锁。
    /// </summary>
    private readonly SemaphoreSlim _commandGate = new(1, 1);

    /// <summary>
    /// 保护单次资源释放的锁。
    /// </summary>
    private readonly object _disposeSync = new();

    /// <summary>
    /// 取消绑定跳转任务的信号源。
    /// </summary>
    private readonly CancellationTokenSource _bindingSeekCts = new();

    /// <summary>
    /// 创建播放器时捕获的通知线程上下文。
    /// </summary>
    private readonly SynchronizationContext? _notificationContext;

    /// <summary>
    /// 确保多次释放共享同一个完成任务。
    /// </summary>
    private Task? _disposeTask;

    /// <summary>
    /// 供界面绑定的播放位置（秒）。
    /// </summary>
    private double _positionSeconds;

    /// <summary>
    /// 供界面绑定的媒体总时长（秒）。
    /// </summary>
    private double _durationSeconds = 1;

    /// <summary>
    /// 界面绑定的播放速度。
    /// </summary>
    private double _speed;

    /// <summary>
    /// 当前音量，范围为 0 到 1。
    /// </summary>
    private double _volume;

    /// <summary>
    /// 是否处于静音状态。
    /// </summary>
    private bool _isMuted;

    /// <summary>
    /// 是否存在尚未完成的绑定跳转请求。
    /// </summary>
    private int _bindingSeekPending;

    /// <summary>
    /// 是否已释放资源。
    /// </summary>
    private volatile bool _disposed;

    /// <summary>
    /// 绑定一个由引擎创建的独立播放会话。
    /// </summary>
    internal Player(IPlaybackSession session)
    {
        _session = session;
        _notificationContext = SynchronizationContext.Current;
        _positionSeconds = session.CurrentTime.TotalSeconds;
        _durationSeconds = Math.Max(1, session.Duration?.TotalSeconds ?? 1);
        _speed = session.PlaybackRate;
        _volume = session.Volume;
        _isMuted = session.IsMuted;
        _session.StateChanged += OnSessionStateChanged;
        _session.Faulted += OnSessionFaulted;
        _session.PositionChanged += OnSessionPositionChanged;
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
    /// 当前播放位置；设置时异步跳转。
    /// </summary>
    public TimeSpan Position
    {
        get => TimeSpan.FromSeconds(_positionSeconds);
        set => PositionSeconds = value.TotalSeconds;
    }

    /// <summary>
    /// 媒体总时长，未知时为空。
    /// </summary>
    public TimeSpan? Duration => _session.Duration;

    /// <summary>
    /// 核心确认当前源属于直播。
    /// </summary>
    public bool IsLive => _session.IsLive;

    /// <summary>
    /// 是否允许跳转，供自定义界面绑定。
    /// </summary>
    public bool CanSeek => !_disposed && _session.CanSeek;

    /// <summary>
    /// 是否仍有跳转执行或绑定请求等待完成。
    /// </summary>
    public bool IsSeeking => State == PlaybackState.Seeking || Volatile.Read(ref _bindingSeekPending) > 0;

    /// <summary>
    /// 供进度条绑定的位置，单位为秒。
    /// </summary>
    public double PositionSeconds
    {
        get => _positionSeconds;
        set
        {
            if (!CanSeek)
            {
                FluxMediaLog.Write(LogLevel.Warning, nameof(Player), InstanceId,
                    $"Event=SeekBindingRejected IsLive={IsLive} State={State} RequestedSeconds={value}");
                return;
            }
            if (double.IsNaN(value) || double.IsInfinity(value)) return;
            var normalized = Math.Max(0, Math.Min(DurationSeconds, value));
            if (Math.Abs(normalized - _positionSeconds) < 0.001) return;
            _positionSeconds = normalized;
            ScheduleBindingSeek(normalized);
            RaiseProperties(nameof(Position), nameof(PositionSeconds), nameof(PositionText), nameof(TimeText));
        }
    }

    /// <summary>
    /// 供进度条绑定的总时长，最小为一秒。
    /// </summary>
    public double DurationSeconds => _durationSeconds;

    /// <summary>
    /// 当前播放位置的显示文本。
    /// </summary>
    public string PositionText => FormatTime(Position);

    /// <summary>
    /// 媒体总时长的显示文本。
    /// </summary>
    public string DurationText => Duration.HasValue ? FormatTime(Duration.Value) : "--:--:--";

    /// <summary>
    /// 播放位置与总时长的组合文本。
    /// </summary>
    public string TimeText => $"{PositionText} / {DurationText}";

    /// <summary>
    /// 供界面绑定的播放速度。
    /// </summary>
    public double Speed
    {
        get => _speed;
        set
        {
            var normalized = Math.Max(0.25, Math.Min(4, value));
            if (Math.Abs(normalized - _speed) < 0.0001) return;
            _speed = normalized;
            RaiseProperties(nameof(Speed));
            _ = SetSpeedFromBindingAsync(normalized);
        }
    }

    /// <summary>
    /// 供界面绑定的音量，范围为 0 到 1。
    /// </summary>
    public double Volume
    {
        get => _volume;
        set
        {
            var normalized = Math.Max(0, Math.Min(1, value));
            if (Math.Abs(normalized - _volume) < 0.0001) return;
            _volume = normalized;
            RaiseProperties(nameof(Volume));
            _ = SetVolumeFromBindingAsync(normalized);
        }
    }

    /// <summary>
    /// 供界面绑定的静音状态。
    /// </summary>
    public bool IsMuted
    {
        get => _isMuted;
        set
        {
            if (value == _isMuted) return;
            _isMuted = value;
            RaiseProperties(nameof(IsMuted));
            _ = SetMutedFromBindingAsync(value);
        }
    }

    /// <summary>
    /// 当前录制的诊断信息。
    /// </summary>
    public PlaybackRecordingDiagnosticInfo Recording => _session.GetRecordingDiagnosticInfo();

    /// <summary>
    /// 当前录制的运行状态。
    /// </summary>
    public PlaybackRecordingState RecordingState => Recording.State;

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
    /// 绑定属性值变化时发出的通知。
    /// </summary>
    public event PropertyChangedEventHandler? PropertyChanged;

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
        var source = IsNetworkUrl(pathOrUrl) ? MediaSource.FromNetwork(pathOrUrl) : MediaSource.FromFile(pathOrUrl);
        await OpenAsync(source, options, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 打开媒体源并读取媒体信息。
    /// </summary>
    public async ValueTask OpenAsync(MediaSource source, PlaybackOptions? options = null, CancellationToken cancellationToken = default)
    {
        await _commandGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _session.OpenAsync(new PlaybackOpenRequest(source, options), cancellationToken).ConfigureAwait(false);
            RefreshMediaProperties();
        }
        finally
        {
            _commandGate.Release();
        }
    }

    /// <summary>
    /// 打开本地路径或网络地址并立即开始播放。
    /// </summary>
    public async ValueTask<PlaybackResult> OpenAndPlayAsync(string pathOrUrl, PlaybackOptions? options = null, CancellationToken cancellationToken = default)
    {
        var source = IsNetworkUrl(pathOrUrl) ? MediaSource.FromNetwork(pathOrUrl) : MediaSource.FromFile(pathOrUrl);
        return await OpenAndPlayAsync(source, options, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 打开媒体源并开始播放。
    /// </summary>
    public async ValueTask<PlaybackResult> OpenAndPlayAsync(MediaSource source, PlaybackOptions? options = null, CancellationToken cancellationToken = default)
    {
        await _commandGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _session.OpenAsync(new PlaybackOpenRequest(source, options), cancellationToken).ConfigureAwait(false);
            RefreshMediaProperties();
            return await _session.PlayAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _commandGate.Release();
        }
    }

    /// <summary>
    /// 打开网络媒体源。
    /// </summary>
    public async ValueTask OpenUrlAsync(string url, PlaybackOptions? options = null, CancellationToken cancellationToken = default)
    {
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
    /// 设置速度并更新界面绑定。
    /// </summary>
    public ValueTask<PlaybackResult> SetSpeedAsync(double speed, CancellationToken cancellationToken = default) => ExecuteCommandAsync<PlaybackResult>(async token =>
        {
            var result = await _session.SetPlaybackRateAsync(speed, token).ConfigureAwait(false);
            _speed = _session.PlaybackRate;
            RaiseProperties(nameof(Speed));
            return result;
        }, cancellationToken);

    /// <summary>
    /// 设置音量并更新界面绑定。
    /// </summary>
    public ValueTask<PlaybackResult> SetVolumeAsync(double volume, CancellationToken cancellationToken = default) => ExecuteCommandAsync<PlaybackResult>(async token =>
        {
            var result = await _session.SetVolumeAsync(volume, token).ConfigureAwait(false);
            _volume = _session.Volume;
            RaiseProperties(nameof(Volume));
            return result;
        }, cancellationToken);

    /// <summary>
    /// 设置静音并更新界面绑定。
    /// </summary>
    public ValueTask<PlaybackResult> SetMutedAsync(bool muted, CancellationToken cancellationToken = default) => ExecuteCommandAsync<PlaybackResult>(async token =>
        {
            var result = await _session.SetMutedAsync(muted, token).ConfigureAwait(false);
            _isMuted = _session.IsMuted;
            RaiseProperties(nameof(IsMuted));
            return result;
        }, cancellationToken);

    /// <summary>
    /// 按实际设备状态切换静音。
    /// </summary>
    public ValueTask<PlaybackResult> ToggleMutedAsync(CancellationToken cancellationToken = default) => ExecuteCommandAsync<PlaybackResult>(async token =>
        {
            var result = await _session.SetMutedAsync(!_session.IsMuted, token).ConfigureAwait(false);
            _isMuted = _session.IsMuted;
            RaiseProperties(nameof(IsMuted));
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
    public async ValueTask<PlaybackResult> StartRecordingAsync(PlaybackRecordingOptions options, CancellationToken cancellationToken = default)
    {
        await _commandGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var result = await _session.StartRecordingAsync(options, cancellationToken).ConfigureAwait(false);
            RaiseRecordingProperties();
            return result;
        }
        finally
        {
            _commandGate.Release();
        }
    }

    /// <summary>
    /// 停止并封装当前录制文件。
    /// </summary>
    public async ValueTask<PlaybackResult> StopRecordingAsync(CancellationToken cancellationToken = default)
    {
        await _commandGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var result = await _session.StopRecordingAsync(cancellationToken).ConfigureAwait(false);
            RaiseRecordingProperties();
            return result;
        }
        finally
        {
            _commandGate.Release();
        }
    }

    /// <summary>
    /// 获取当前运行状态和诊断信息。
    /// </summary>
    public PlaybackDiagnosticInfo GetDiagnosticInfo()
    {
        return _session.GetDiagnosticInfo();
    }

    /// <summary>
    /// 获取当前录制状态、输出文件和最后错误。
    /// </summary>
    public PlaybackRecordingDiagnosticInfo GetRecordingDiagnosticInfo()
    {
        return _session.GetRecordingDiagnosticInfo();
    }

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
        lock (_disposeSync)
        {
            _disposeTask ??= DisposeCoreAsync();
            return new ValueTask(_disposeTask);
        }
    }

    /// <summary>
    /// 取消绑定跳转，并等待命令结束后释放会话。
    /// </summary>
    private async Task DisposeCoreAsync()
    {
        _disposed = true;
        _bindingSeekCts.Cancel();
        _session.CancelPendingOperations();
        try
        {
            await _commandGate.WaitAsync().ConfigureAwait(false);
            try
            {
                _session.StateChanged -= OnSessionStateChanged;
                _session.Faulted -= OnSessionFaulted;
                _session.PositionChanged -= OnSessionPositionChanged;
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
            _bindingSeekCts.Dispose();
        }
    }

    /// <summary>
    /// 判断地址是否属于支持的网络协议。
    /// </summary>
    private static bool IsNetworkUrl(string value)
    {
        return Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && (uri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase)
                || uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase)
                || uri.Scheme.Equals("rtsp", StringComparison.OrdinalIgnoreCase)
                || uri.Scheme.Equals("rtmp", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 进度绑定直接提交给唯一的跳转调度器。
    /// </summary>
    private void ScheduleBindingSeek(double seconds)
    {
        if (!CanSeek) return;
        _ = ApplyBindingSeekAsync(seconds);
    }

    /// <summary>
    /// 应用绑定的速度值，异常时恢复会话值。
    /// </summary>
    private async Task SetSpeedFromBindingAsync(double speed)
    {
        if (_disposed) return;
        try
        {
            await SetSpeedAsync(speed).ConfigureAwait(false);
        }
        catch (Exception)
        {
            if (!_disposed)
            {
                _speed = _session.PlaybackRate;
                RaiseProperties(nameof(Speed));
            }
        }
    }

    /// <summary>
    /// 应用绑定的音量值，异常时恢复会话值。
    /// </summary>
    private async Task SetVolumeFromBindingAsync(double volume)
    {
        if (_disposed) return;
        try
        {
            await SetVolumeAsync(volume).ConfigureAwait(false);
        }
        catch (Exception)
        {
            if (!_disposed)
            {
                _volume = _session.Volume;
                RaiseProperties(nameof(Volume));
            }
        }
    }

    /// <summary>
    /// 应用绑定的静音值，异常时恢复会话值。
    /// </summary>
    private async Task SetMutedFromBindingAsync(bool muted)
    {
        if (_disposed) return;
        try
        {
            await SetMutedAsync(muted).ConfigureAwait(false);
        }
        catch (Exception)
        {
            if (!_disposed)
            {
                _isMuted = _session.IsMuted;
                RaiseProperties(nameof(IsMuted));
            }
        }
    }

    /// <summary>
    /// 转发会话状态并更新播放状态属性。
    /// </summary>
    private void OnSessionStateChanged(object? sender, PlaybackState state)
    {
        DispatchNotification(() =>
        {
            NotifySubscribers(StateChanged, state, nameof(StateChanged));
            RaisePropertiesCore(nameof(State), nameof(IsPlaying), nameof(IsSeeking), nameof(CanSeek), nameof(IsLive));
        });
    }

    /// <summary>
    /// 转发底层会话的错误通知。
    /// </summary>
    private void OnSessionFaulted(object? sender, FluxMediaError error) => DispatchNotification(() => NotifySubscribers(Faulted, error, nameof(Faulted)));

    /// <summary>
    /// 将底层视频帧通知转发给显示端。
    /// </summary>
    private void OnSessionVideoFrameReady(object? sender, PlaybackVideoFrameEventArgs args) => NotifySubscribers(VideoFrameReady, args, nameof(VideoFrameReady));

    /// <summary>
    /// 同步会话进度并刷新界面绑定。
    /// </summary>
    private void OnSessionPositionChanged(object? sender, PlaybackPositionChangedEventArgs args)
    {
        DispatchNotification(() =>
        {
            NotifySubscribers(PositionChanged, args, nameof(PositionChanged));
            if (Volatile.Read(ref _bindingSeekPending) == 0)
            {
                _positionSeconds = args.Position.TotalSeconds;
            }
            _durationSeconds = Math.Max(1, args.Duration?.TotalSeconds ?? 1);
            RaisePropertiesCore(
                nameof(Position), nameof(PositionSeconds), nameof(PositionText), nameof(TimeText));
        });
    }

    /// <summary>
    /// 将多个属性变更通知派发到界面上下文。
    /// </summary>
    private void RaiseProperties(params string[] propertyNames)
    {
        DispatchNotification(() => RaisePropertiesCore(propertyNames));
    }

    /// <summary>
    /// 逐个触发属性变更事件。
    /// </summary>
    private void RaisePropertiesCore(params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            var args = PropertyChangedArgs.GetOrAdd(propertyName, static name => new PropertyChangedEventArgs(name));
            if (PropertyChanged is null) continue;
            foreach (PropertyChangedEventHandler handler in PropertyChanged.GetInvocationList())
            {
                try
                {
                    handler(this, args);
                }
                catch (Exception exception)
                {
                    FluxMediaLog.Write(LogLevel.Warning, nameof(Player), InstanceId, "Event=PropertyChangedSubscriberFailed", exception);
                }
            }
        }
    }

    /// <summary>
    /// 在创建播放器时捕获的线程上下文中通知界面。
    /// </summary>
    private void DispatchNotification(Action notification)
    {
        if (_disposed) return;
        var context = _notificationContext;
        if (context is null || ReferenceEquals(context, SynchronizationContext.Current))
        {
            notification();
            return;
        }

        context.Post(static state =>
        {
            var (owner, action) = ((Player, Action))state!;
            if (!owner._disposed) action();
        }, (this, notification));
    }

    /// <summary>
    /// 隔离每个订阅者的异常，避免影响其它显示端或通知。
    /// </summary>
    private void NotifySubscribers<TEvent>(EventHandler<TEvent>? handlers, TEvent value, string eventName)
    {
        if (handlers is null || _disposed) return;
        foreach (EventHandler<TEvent> handler in handlers.GetInvocationList())
        {
            try { handler(this, value); }
            catch (Exception exception)
            {
                FluxMediaLog.Write(LogLevel.Warning, nameof(Player), InstanceId, $"Event=SubscriberFailed Notification={eventName}", exception);
            }
        }
    }

    /// <summary>
    /// 用会话中的实际进度更新绑定值。
    /// </summary>
    private void RefreshPositionFromSession()
    {
        _positionSeconds = _session.CurrentTime.TotalSeconds;
        _durationSeconds = Math.Max(1, _session.Duration?.TotalSeconds ?? 1);
        RaiseProperties(
            nameof(CanSeek), nameof(IsLive), nameof(IsSeeking), nameof(Position), nameof(Duration), nameof(PositionSeconds), nameof(DurationSeconds), nameof(PositionText), nameof(DurationText), nameof(TimeText));
    }

    /// <summary>
    /// 更新录制状态相关的绑定属性。
    /// </summary>
    private void RaiseRecordingProperties() => RaiseProperties(nameof(Recording), nameof(RecordingState), nameof(IsRecording));

    /// <summary>
    /// 刷新媒体信息和进度绑定属性。
    /// </summary>
    private void RefreshMediaProperties()
    {
        _positionSeconds = _session.CurrentTime.TotalSeconds;
        _durationSeconds = Math.Max(1, _session.Duration?.TotalSeconds ?? 1);
        RaiseProperties(
            nameof(Source), nameof(CanSeek), nameof(IsLive), nameof(IsSeeking), nameof(Position), nameof(Duration), nameof(PositionSeconds), nameof(DurationSeconds), nameof(PositionText), nameof(DurationText), nameof(TimeText));
    }

    /// <summary>
    /// 将媒体时间格式化为时分秒。
    /// </summary>
    private static string FormatTime(TimeSpan value)
    {
        var totalHours = (long)value.TotalHours;
        return $"{totalHours:00}:{value.Minutes:00}:{value.Seconds:00}";
    }
    /// <summary>
    /// 序列化普通命令；跳转请求由专用调度器负责合并。
    /// </summary>
    private async ValueTask<TResult> ExecuteCommandAsync<TResult>(Func<CancellationToken, ValueTask<TResult>> command, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await _commandGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { ThrowIfDisposed(); return await command(cancellationToken).ConfigureAwait(false); }
        finally { _commandGate.Release(); }
    }
    /// <summary>
    /// 序列化不返回结果的命令。
    /// </summary>
    private async ValueTask ExecuteCommandAsync(Func<CancellationToken, ValueTask> command, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await _commandGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { ThrowIfDisposed(); await command(cancellationToken).ConfigureAwait(false); }
        finally { _commandGate.Release(); }
    }
    /// <summary>
    /// 跟踪绑定请求，失败时恢复到实际播放位置。
    /// </summary>
    private async Task ApplyBindingSeekAsync(double seconds)
    {
        if (Interlocked.Increment(ref _bindingSeekPending) == 1) RaiseProperties(nameof(IsSeeking));
        try
        {
            FluxMediaLog.Write(LogLevel.Information, nameof(Player), InstanceId,
                $"Event=SeekBindingSubmitted TargetSeconds={seconds} IsLive={IsLive}");
            var result = await SeekAsync(TimeSpan.FromSeconds(seconds), _bindingSeekCts.Token).ConfigureAwait(false);
            if (!result.IsSuccess)
                FluxMediaLog.Write(LogLevel.Warning, nameof(Player), InstanceId,
                    $"Event=SeekBindingFailed TargetSeconds={seconds} Error={result.Error}");
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) when (_disposed) { }
        catch (Exception exception)
        {
            FluxMediaLog.Write(LogLevel.Warning, nameof(Player), InstanceId,
                $"Event=SeekBindingFailed TargetSeconds={seconds} ExceptionType={exception.GetType().Name}");
        }
        finally
        {
            if (Interlocked.Decrement(ref _bindingSeekPending) == 0 && !_disposed)
            {
                RefreshPositionFromSession();
                RaiseProperties(nameof(IsSeeking));
            }
        }
    }
    /// <summary>
    /// 释放开始后拒绝新命令。
    /// </summary>
    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(Player));
    }

}
