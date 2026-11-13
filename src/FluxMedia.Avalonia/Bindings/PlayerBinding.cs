using Avalonia.Threading;
using FluxMedia.Core.Configuration;
using FluxMedia.Core.Logging;
using FluxMedia.Core.Models;
using FluxMedia.Playback;
using System.Collections.Concurrent;
using System.ComponentModel;

namespace FluxMedia.Avalonia.Bindings;

/// <summary>
/// 适配播放器的界面绑定，只借用播放器，不管理其生命周期。
/// </summary>
public sealed class PlayerBinding : INotifyPropertyChanged, IDisposable
{
    /// <summary>
    /// 复用属性变更参数。
    /// </summary>
    private static readonly ConcurrentDictionary<string, PropertyChangedEventArgs> PropertyArgs = new(StringComparer.Ordinal);

    /// <summary>
    /// 最近观察到的媒体源，用于识别切源。
    /// </summary>
    private MediaSource? _source;

    /// <summary>
    /// 当前进度或拖动预览值。
    /// </summary>
    private double _positionSeconds;

    /// <summary>
    /// 最近已通知界面的进度。
    /// </summary>
    private TimeSpan _publishedPosition;

    /// <summary>
    /// 最近已通知界面的媒体时长。
    /// </summary>
    private TimeSpan? _duration;

    /// <summary>
    /// 进度条采用的总秒数。
    /// </summary>
    private double _durationSeconds;

    /// <summary>
    /// 最近显示的整秒进度。
    /// </summary>
    private long _displaySecond;

    /// <summary>
    /// 缓存的进度文本。
    /// </summary>
    private string _positionText;

    /// <summary>
    /// 缓存的总时长文本。
    /// </summary>
    private string _durationText;

    /// <summary>
    /// 缓存的组合时间文本。
    /// </summary>
    private string _timeText;

    /// <summary>
    /// 界面请求的音量。
    /// </summary>
    private double _volume;

    /// <summary>
    /// 界面请求的静音状态。
    /// </summary>
    private bool _isMuted;

    /// <summary>
    /// 最近已通知的播放状态和操作权限。
    /// </summary>
    private (PlaybackState State, bool IsDisposed, bool IsLive, bool CanSeek, bool IsSeeking, bool CanChangeSpeed) _publishedState;

    /// <summary>
    /// 最近已通知的实际倍速和请求状态。
    /// </summary>
    private (double Speed, double RequestedSpeed, bool IsSpeedChangePending, string? LastSpeedError) _publishedRate;

    /// <summary>
    /// 最近已通知的音量和静音状态。
    /// </summary>
    private (double Volume, bool IsMuted) _publishedAudio;

    /// <summary>
    /// 尚未完成的拖动请求数。
    /// </summary>
    private int _pendingSeeks;

    /// <summary>
    /// 尚未完成的音量赋值数。
    /// </summary>
    private int _pendingVolume;

    /// <summary>
    /// 尚未完成的静音赋值数。
    /// </summary>
    private int _pendingMute;

    /// <summary>
    /// 是否已排队一次界面刷新。
    /// </summary>
    private int _updateQueued;

    /// <summary>
    /// 排队期间是否发生控制状态变化。
    /// </summary>
    private int _controlUpdate;

    /// <summary>
    /// 重开相同媒体对象时也刷新来源与旧错误。
    /// </summary>
    private int _mediaOpening;

    /// <summary>
    /// 属性回写期间不再次提交命令。
    /// </summary>
    private bool _publishing;

    /// <summary>
    /// 释放后忽略排队通知和异步完成。
    /// </summary>
    private volatile bool _disposed;

    /// <summary>
    /// 在界面线程创建并订阅核心事件，使用结束后须 Dispose。
    /// </summary>
    public PlayerBinding(Player player)
    {
        Dispatcher.UIThread.VerifyAccess();
        Player = player ?? throw new ArgumentNullException(nameof(player));
        _source = player.Source;
        _publishedPosition = player.Position;
        _positionSeconds = _publishedPosition.TotalSeconds;
        _duration = player.Duration;
        _durationSeconds = Math.Max(1, _duration?.TotalSeconds ?? 1);
        _displaySecond = _publishedPosition.Ticks / TimeSpan.TicksPerSecond;
        _positionText = FormatTime(_publishedPosition);
        _durationText = _duration is { } duration ? FormatTime(duration) : "--:--:--";
        _timeText = $"{_positionText} / {_durationText}";
        _volume = player.Volume;
        _isMuted = player.IsMuted;
        _publishedState = (State, IsDisposed, IsLive, CanSeek, IsSeeking, CanChangeSpeed);
        _publishedRate = (Speed, RequestedSpeed, IsSpeedChangePending, LastSpeedError);
        _publishedAudio = (Volume, IsMuted);
        player.PlaybackChanged += OnPlaybackChanged;
        player.PositionChanged += OnPositionChanged;
    }

    /// <summary>
    /// 实际执行播放操作的核心对象。
    /// </summary>
    public Player Player { get; }

    /// <summary>
    /// 当前媒体源。
    /// </summary>
    public MediaSource? Source => Player.Source;

    /// <summary>
    /// 当前播放状态。
    /// </summary>
    public PlaybackState State => Player.State;

    /// <summary>
    /// 核心是否正在播放。
    /// </summary>
    public bool IsPlaying => Player.IsPlaying;

    /// <summary>
    /// 核心或适配器是否已释放。
    /// </summary>
    public bool IsDisposed => _disposed || Player.IsDisposed;

    /// <summary>
    /// 当前源是否为直播。
    /// </summary>
    public bool IsLive => Player.IsLive;

    /// <summary>
    /// 核心提供的跳转权限。
    /// </summary>
    public bool CanSeek => !IsDisposed && Player.CanSeek;

    /// <summary>
    /// 跳转执行中或界面请求等待完成。
    /// </summary>
    public bool IsSeeking => Player.IsSeeking || _pendingSeeks > 0;

    /// <summary>
    /// 核心报告的媒体总时长。
    /// </summary>
    public TimeSpan? Duration => _duration;

    /// <summary>
    /// 可绑定的进度预览；真实位置从 Player.Position 读取。
    /// </summary>
    public TimeSpan Position
    {
        get => TimeSpan.FromSeconds(_positionSeconds);
        set => PositionSeconds = value.TotalSeconds;
    }

    /// <summary>
    /// 进度条总时长，未知时使用一秒占位。
    /// </summary>
    public double DurationSeconds => _durationSeconds;

    /// <summary>
    /// 可双向绑定的进度，拖动时立即提交核心 Seek。
    /// </summary>
    public double PositionSeconds
    {
        get => _positionSeconds;
        set
        {
            if (_disposed || _publishing || !CanSeek || double.IsNaN(value) || double.IsInfinity(value)) return;
            var target = Math.Max(0, Math.Min(DurationSeconds, value));
            if (Math.Abs(target - _positionSeconds) < 0.001) return;
            _positionSeconds = target;
            _ = ApplySeekAsync(target);
        }
    }

    /// <summary>
    /// 当前进度的显示文本。
    /// </summary>
    public string PositionText => _positionText;

    /// <summary>
    /// 总时长的显示文本。
    /// </summary>
    public string DurationText => _durationText;

    /// <summary>
    /// 当前进度与总时长的组合文本。
    /// </summary>
    public string TimeText => _timeText;

    /// <summary>
    /// 核心提供的倍速权限。
    /// </summary>
    public bool CanChangeSpeed => !IsDisposed && Player.CanChangeSpeed;

    /// <summary>
    /// 倍速只在核心确认后更新。
    /// </summary>
    public double Speed
    {
        get => Player.Speed;
        set
        {
            if (_disposed || _publishing) return;
            if (!CanChangeSpeed)
            {
                RaiseProperties(nameof(Speed));
                return;
            }
            _ = ApplyControlAsync(() => Player.SetSpeedAsync(value), nameof(Speed));
        }
    }

    /// <summary>
    /// 最近请求的目标倍速。
    /// </summary>
    public double RequestedSpeed => Player.RequestedSpeed;

    /// <summary>
    /// 目标倍速是否仍在等待应用。
    /// </summary>
    public bool IsSpeedChangePending => Player.IsSpeedChangePending;

    /// <summary>
    /// 核心报告的最近换速错误。
    /// </summary>
    public string? LastSpeedError => Player.LastSpeedError;

    /// <summary>
    /// 当前录制诊断；媒体切换时可能为空。
    /// </summary>
    public PlaybackRecordingDiagnosticInfo? Recording => Player.Recording;

    /// <summary>
    /// 当前录制状态。
    /// </summary>
    public PlaybackRecordingState RecordingState => Player.RecordingState;

    /// <summary>
    /// 核心是否正在录制。
    /// </summary>
    public bool IsRecording => Player.IsRecording;

    /// <summary>
    /// 可双向绑定的音量。
    /// </summary>
    public double Volume
    {
        get => _volume;
        set
        {
            if (_disposed || _publishing || double.IsNaN(value) || double.IsInfinity(value)) return;
            var target = Math.Max(0, Math.Min(1, value));
            if (Math.Abs(target - _volume) < 0.0001) return;
            _volume = target;
            _ = ApplyControlAsync(() => Player.SetVolumeAsync(target), nameof(Volume));
        }
    }

    /// <summary>
    /// 可双向绑定的静音状态。
    /// </summary>
    public bool IsMuted
    {
        get => _isMuted;
        set
        {
            if (_disposed || _publishing || value == _isMuted) return;
            _isMuted = value;
            _ = ApplyControlAsync(() => Player.SetMutedAsync(value), nameof(IsMuted));
        }
    }

    /// <summary>
    /// 界面线程中的属性通知。
    /// </summary>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// 控件刷新命令与显隐，参数表示媒体源是否变化。
    /// </summary>
    internal event Action<bool>? Updated;

    /// <summary>
    /// 合并核心控制状态通知。
    /// </summary>
    private void OnPlaybackChanged(object? sender, EventArgs args)
    {
        if (Player.State == PlaybackState.Opening) Interlocked.Exchange(ref _mediaOpening, 1);
        QueueUpdate(controlChanged: true);
    }

    /// <summary>
    /// 合并进度通知，不将视频帧引入绑定层。
    /// </summary>
    private void OnPositionChanged(object? sender, PlaybackPositionChangedEventArgs args) => QueueUpdate(controlChanged: false);

    /// <summary>
    /// 一次排队读取最新状态，避免重复切换 UI 线程。
    /// </summary>
    private void QueueUpdate(bool controlChanged)
    {
        if (_disposed) return;
        if (controlChanged) Interlocked.Exchange(ref _controlUpdate, 1);
        if (Interlocked.Exchange(ref _updateQueued, 1) != 0) return;
        Dispatcher.UIThread.Post(() =>
        {
            Interlocked.Exchange(ref _updateQueued, 0);
            if (!_disposed) Refresh(Interlocked.Exchange(ref _controlUpdate, 0) != 0);
        });
    }

    /// <summary>
    /// 回写实际值，保留尚未完成的拖动与音量预览。
    /// </summary>
    private void Refresh(bool controlChanged, string? resetProperty = null)
    {
        var sourceChanged = Interlocked.Exchange(ref _mediaOpening, 0) != 0 || !ReferenceEquals(_source, Player.Source);
        _source = Player.Source;
        if (_pendingSeeks == 0 || sourceChanged) _positionSeconds = Player.Position.TotalSeconds;
        if (_pendingVolume == 0) _volume = Player.Volume;
        if (_pendingMute == 0) _isMuted = Player.IsMuted;
        PublishPosition(resetProperty == nameof(PositionSeconds));
        if (!controlChanged && !sourceChanged) return;
        if (sourceChanged) RaiseProperties(nameof(Source));
        PublishControlState(resetProperty);
        NotifyUpdated(sourceChanged);
    }

    /// <summary>
    /// 按组通知控制变化，命令完成后保留绑定回读。
    /// </summary>
    private void PublishControlState(string? resetProperty = null)
    {
        var state = (State, IsDisposed, IsLive, CanSeek, IsSeeking, CanChangeSpeed);
        var rate = (Speed, RequestedSpeed, IsSpeedChangePending, LastSpeedError);
        var audio = (Volume, IsMuted);
        var previousState = _publishedState;
        var previousRate = _publishedRate;
        var previousAudio = _publishedAudio;
        _publishedState = state;
        _publishedRate = rate;
        _publishedAudio = audio;

        if (state != previousState)
            RaiseProperties(nameof(State), nameof(IsPlaying), nameof(IsDisposed), nameof(IsLive), nameof(CanSeek), nameof(IsSeeking), nameof(CanChangeSpeed));
        if (rate != previousRate || resetProperty == nameof(Speed))
            RaiseProperties(nameof(Speed), nameof(RequestedSpeed), nameof(IsSpeedChangePending), nameof(LastSpeedError));
        if (audio != previousAudio || resetProperty is nameof(Volume) or nameof(IsMuted))
            RaiseProperties(nameof(Volume), nameof(IsMuted));
        // 录制快照会查询文件信息，此处只通知，由订阅方按需读取。
        RaiseProperties(nameof(Recording), nameof(RecordingState), nameof(IsRecording));
    }

    /// <summary>
    /// 数值按实际变化通知，显示文本仅在可见时间变化时生成。
    /// </summary>
    private void PublishPosition(bool resetPosition = false)
    {
        var position = TimeSpan.FromSeconds(_positionSeconds);
        var positionChanged = position != _publishedPosition;
        _publishedPosition = position;
        var duration = Player.Duration;
        var durationChanged = duration != _duration;
        _duration = duration;
        var durationSeconds = Math.Max(1, duration?.TotalSeconds ?? 1);
        var sliderMaximumChanged = durationSeconds != _durationSeconds;
        _durationSeconds = durationSeconds;
        var second = position.Ticks / TimeSpan.TicksPerSecond;
        var positionTextChanged = second != _displaySecond;
        _displaySecond = second;
        if (positionTextChanged) _positionText = FormatTime(position);
        var durationTextChanged = false;
        if (durationChanged)
        {
            var text = duration is { } value ? FormatTime(value) : "--:--:--";
            durationTextChanged = text != _durationText;
            _durationText = text;
        }
        var timeTextChanged = positionTextChanged || durationTextChanged;
        if (timeTextChanged) _timeText = $"{_positionText} / {_durationText}";
        // 先更新全部缓存，再通知订阅者，避免读到半更新的组合文本。
        if (positionChanged || resetPosition) RaiseProperties(nameof(Position), nameof(PositionSeconds));
        if (durationChanged) RaiseProperties(nameof(Duration));
        if (sliderMaximumChanged) RaiseProperties(nameof(DurationSeconds));
        if (positionTextChanged) RaiseProperties(nameof(PositionText));
        if (durationTextChanged) RaiseProperties(nameof(DurationText));
        if (timeTextChanged) RaiseProperties(nameof(TimeText));
    }

    /// <summary>
    /// 进度赋值只提交目标，合并与权限检查由核心执行。
    /// </summary>
    private async Task ApplySeekAsync(double seconds)
    {
        _pendingSeeks++;
        PublishPosition();
        var wasSeeking = _publishedState.IsSeeking;
        _publishedState.IsSeeking = IsSeeking;
        if (_publishedState.IsSeeking != wasSeeking) RaiseProperties(nameof(IsSeeking));
        NotifyUpdated(false);
        try
        {
            FluxMediaLog.Write(LogLevel.Information, nameof(PlayerBinding), Player.InstanceId, $"Event=SeekBindingSubmitted TargetSeconds={seconds}");
            var result = await Player.SeekAsync(TimeSpan.FromSeconds(seconds)).ConfigureAwait(false);
            if (!result.IsSuccess) LogFailure(nameof(PositionSeconds), result.Message ?? result.Error.ToString());
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { LogFailure(nameof(PositionSeconds), exception.Message, exception); }
        finally
        {
            Dispatch(() =>
            {
                _pendingSeeks--;
                Refresh(controlChanged: true, resetProperty: nameof(PositionSeconds));
            });
        }
    }

    /// <summary>
    /// 绑定命令保留异步结果，失败时回读核心值。
    /// </summary>
    private async Task ApplyControlAsync(Func<ValueTask<PlaybackResult>> operation, string propertyName)
    {
        if (propertyName == nameof(Volume)) _pendingVolume++;
        if (propertyName == nameof(IsMuted)) _pendingMute++;
        try
        {
            var result = await operation().ConfigureAwait(false);
            if (!result.IsSuccess) LogFailure(propertyName, result.Message ?? result.Error.ToString());
        }
        catch (Exception exception) { LogFailure(propertyName, exception.Message, exception); }
        finally
        {
            Dispatch(() =>
            {
                if (propertyName == nameof(Volume)) _pendingVolume--;
                if (propertyName == nameof(IsMuted)) _pendingMute--;
                Refresh(controlChanged: true, resetProperty: propertyName);
            });
        }
    }

    /// <summary>
    /// 仅在界面线程发布，旧适配器完成后不再更新页面。
    /// </summary>
    private void Dispatch(Action action)
    {
        if (_disposed) return;
        if (Dispatcher.UIThread.CheckAccess()) action();
        else Dispatcher.UIThread.Post(() => { if (!_disposed) action(); });
    }

    /// <summary>
    /// 隔离订阅异常，并防止回写属性产生新的控制请求。
    /// </summary>
    private void RaiseProperties(params string[] names)
    {
        if (_disposed) return;
        var previous = _publishing;
        _publishing = true;
        try
        {
            PropertyChangedEventHandler? subscription = null;
            var handlers = Array.Empty<Delegate>();
            foreach (var name in names)
            {
                if (_disposed) return;
                var current = PropertyChanged;
                if (current is null) continue;
                if (!ReferenceEquals(subscription, current))
                {
                    subscription = current;
                    handlers = current.GetInvocationList();
                }
                var args = PropertyArgs.GetOrAdd(name, static value => new PropertyChangedEventArgs(value));
                foreach (PropertyChangedEventHandler handler in handlers)
                {
                    try { handler(this, args); }
                    catch (Exception exception) { LogFailure(name, exception.Message, exception); }
                }
            }
        }
        finally { _publishing = previous; }
    }

    /// <summary>
    /// 隔离每个界面更新订阅者，防止异常冒出 UI 派发线程。
    /// </summary>
    private void NotifyUpdated(bool sourceChanged)
    {
        if (_disposed || Updated is null) return;
        foreach (Action<bool> handler in Updated.GetInvocationList())
        {
            if (_disposed) return;
            try { handler(sourceChanged); }
            catch (Exception exception) { LogFailure(nameof(Updated), exception.Message, exception); }
        }
    }

    /// <summary>
    /// 记录绑定操作错误，不将其转换为播放故障。
    /// </summary>
    private void LogFailure(string propertyName, string message, Exception? exception = null)
    {
        if (!_disposed)
            FluxMediaLog.Write(LogLevel.Warning, nameof(PlayerBinding), Player.InstanceId,
                $"Event=PlaybackBindingFailed Property={propertyName} Message={message}", exception);
    }

    /// <summary>
    /// 默认时分秒显示，使用方也可直接绑定数值自行格式化。
    /// </summary>
    private static string FormatTime(TimeSpan value) => $"{(long)value.TotalHours:00}:{value.Minutes:00}:{value.Seconds:00}";

    /// <summary>
    /// 解除界面订阅，已提交的核心操作与播放器继续运行。
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Player.PlaybackChanged -= OnPlaybackChanged;
        Player.PositionChanged -= OnPositionChanged;
    }
}
