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
    /// 界面请求的音量。
    /// </summary>
    private double _volume;

    /// <summary>
    /// 界面请求的静音状态。
    /// </summary>
    private bool _isMuted;

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
        _positionSeconds = player.Position.TotalSeconds;
        _volume = player.Volume;
        _isMuted = player.IsMuted;
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
    public TimeSpan? Duration => Player.Duration;

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
    public double DurationSeconds => Math.Max(1, Player.Duration?.TotalSeconds ?? 1);

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
    public string PositionText => FormatTime(TimeSpan.FromSeconds(_positionSeconds));

    /// <summary>
    /// 总时长的显示文本。
    /// </summary>
    public string DurationText => Player.Duration is { } duration ? FormatTime(duration) : "--:--:--";

    /// <summary>
    /// 当前进度与总时长的组合文本。
    /// </summary>
    public string TimeText => $"{PositionText} / {DurationText}";

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
    /// 当前录制结果，具体数据由核心提供。
    /// </summary>
    public PlaybackRecordingDiagnosticInfo Recording => Player.Recording;

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
    private void Refresh(bool controlChanged)
    {
        var sourceChanged = Interlocked.Exchange(ref _mediaOpening, 0) != 0 || !ReferenceEquals(_source, Player.Source);
        _source = Player.Source;
        if (_pendingSeeks == 0 || sourceChanged) _positionSeconds = Player.Position.TotalSeconds;
        if (_pendingVolume == 0) _volume = Player.Volume;
        if (_pendingMute == 0) _isMuted = Player.IsMuted;
        RaiseProperties(nameof(Position), nameof(Duration), nameof(PositionSeconds), nameof(DurationSeconds), nameof(PositionText), nameof(DurationText), nameof(TimeText));
        if (!controlChanged && !sourceChanged) return;
        if (sourceChanged) RaiseProperties(nameof(Source));
        RaiseProperties(nameof(State), nameof(IsPlaying), nameof(IsDisposed), nameof(IsLive), nameof(CanSeek), nameof(IsSeeking),
            nameof(CanChangeSpeed), nameof(Speed), nameof(RequestedSpeed), nameof(IsSpeedChangePending), nameof(LastSpeedError), nameof(Volume), nameof(IsMuted),
            nameof(Recording), nameof(RecordingState), nameof(IsRecording));
        NotifyUpdated(sourceChanged);
    }

    /// <summary>
    /// 进度赋值只提交目标，合并与权限检查由核心执行。
    /// </summary>
    private async Task ApplySeekAsync(double seconds)
    {
        _pendingSeeks++;
        RaiseProperties(nameof(Position), nameof(PositionSeconds), nameof(PositionText), nameof(TimeText), nameof(IsSeeking));
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
                Refresh(controlChanged: true);
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
                Refresh(controlChanged: true);
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
            foreach (var name in names)
            {
                if (_disposed) return;
                var args = PropertyArgs.GetOrAdd(name, static value => new PropertyChangedEventArgs(value));
                if (PropertyChanged is null) continue;
                foreach (PropertyChangedEventHandler handler in PropertyChanged.GetInvocationList())
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
