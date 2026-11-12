using Avalonia.Threading;
using FluxMedia.Core.Configuration;
using FluxMedia.Core.Errors;
using FluxMedia.Core.Logging;
using FluxMedia.Playback;
using FluxMedia.Avalonia.Bindings;

namespace FluxMedia.Avalonia;

public sealed partial class FluxPlayerControl
{
    /// <summary>
    /// 检查播放或暂停命令是否可用。
    /// </summary>
    private bool CanPlayPause() => _isAttached && !IsBusy && Player is { IsDisposed: false, Source: not null } player
        && player.State is PlaybackState.Ready or PlaybackState.Playing or PlaybackState.Paused
            or PlaybackState.Stopped or PlaybackState.Completed;

    /// <summary>
    /// 检查停止命令是否可用。
    /// </summary>
    private bool CanStop() => _isAttached && !IsBusy && Player is { IsDisposed: false } player
        && player.State is not (PlaybackState.Created or PlaybackState.Stopped or PlaybackState.Stopping or PlaybackState.Disposed);

    /// <summary>
    /// 检查命令状态和核心跳转权限。
    /// </summary>
    private bool CanSeek() => _isAttached && !IsBusy && Player?.CanSeek == true;

    /// <summary>
    /// 执行播放命令并显示失败原因。
    /// </summary>
    private async Task ExecutePlaybackAsync(Func<Player, ValueTask<PlaybackResult>> operation)
    {
        var player = Player;
        if (player is null || player.IsDisposed || IsBusy) return;
        var version = Interlocked.Read(ref _subscriptionVersion);
        SetAndRaise(IsBusyProperty, ref _isBusy, true);
        SetAndRaise(ErrorTextProperty, ref _errorText, string.Empty);
        RefreshCommands();
        ShowControls();
        try
        {
            var result = await operation(player);
            if (!result.IsSuccess && IsCurrentPlayer(player, version))
                ReportError(result.Message ?? result.Error.ToString());
        }
        catch (Exception exception)
        {
            if (IsCurrentPlayer(player, version)) ReportError(exception.Message);
            FluxMediaLog.Write(LogLevel.Warning, nameof(FluxPlayerControl), player.InstanceId,
                "Event=PlaybackControlFailed", exception);
        }
        finally
        {
            if (IsCurrentPlayer(player, version))
            {
                SetAndRaise(IsBusyProperty, ref _isBusy, false);
                RefreshCommands();
                ShowControls();
            }
        }
    }

    /// <summary>
    /// 刷新基础操作命令的可用状态。
    /// </summary>
    private void RefreshCommands()
    {
        SetAndRaise(IsSpeedChangePendingProperty, ref _isSpeedChangePending, Player?.IsSpeedChangePending == true);
        _playPauseCommand.RaiseCanExecuteChanged();
        _stopCommand.RaiseCanExecuteChanged();
        _seekBackwardCommand.RaiseCanExecuteChanged();
        _seekForwardCommand.RaiseCanExecuteChanged();
    }

    /// <summary>
    /// 切换播放器订阅并重置界面状态。
    /// </summary>
    private void SubscribePlayer()
    {
        UnsubscribePlayer();
        if (!_isAttached) return;
        _subscribedPlayer = Player;
        if (_subscribedPlayer is not null)
        {
            var binding = new PlayerBinding(_subscribedPlayer);
            binding.Updated += OnPresentationUpdated;
            SetAndRaise(PresentationProperty, ref _presentation, binding);
            _subscribedPlayer.Faulted += OnPlayerFaulted;
        }
        SetAndRaise(IsBusyProperty, ref _isBusy, false);
        SetAndRaise(ErrorTextProperty, ref _errorText, string.Empty);
        RefreshCommands();
        UpdateControlBar();
        ShowControls();
    }

    /// <summary>
    /// 解除状态订阅并使旧回调失效。
    /// </summary>
    private void UnsubscribePlayer()
    {
        Interlocked.Increment(ref _subscriptionVersion);
        if (_presentation is not null)
        {
            _presentation.Updated -= OnPresentationUpdated;
            _presentation.Dispose();
            SetAndRaise(PresentationProperty, ref _presentation, null);
        }
        if (_subscribedPlayer is not null) _subscribedPlayer.Faulted -= OnPlayerFaulted;
        _subscribedPlayer = null;
        UpdateControlBar();
    }

    /// <summary>
    /// 绑定对象已完成 UI 派发，直接刷新命令和控制栏。
    /// </summary>
    private void OnPresentationUpdated(bool sourceChanged)
    {
        if (!_isAttached) return;
        if (sourceChanged) SetAndRaise(ErrorTextProperty, ref _errorText, string.Empty);
        RefreshCommands();
        ShowControls();
    }

    /// <summary>
    /// 显示当前播放器的故障信息。
    /// </summary>
    private void OnPlayerFaulted(object? sender, FluxMediaError error)
    {
        var version = Interlocked.Read(ref _subscriptionVersion);
        Dispatcher.UIThread.Post(() =>
        {
            if (sender is Player player && IsCurrentPlayer(player, version)) ReportError(error.Message);
        });
    }

    /// <summary>
    /// 校验播放器及订阅代次。
    /// </summary>
    private bool IsCurrentPlayer(Player player, long version) => _isAttached
        && version == Interlocked.Read(ref _subscriptionVersion) && ReferenceEquals(player, Player);

    /// <summary>
    /// 显示错误并展开控制栏。
    /// </summary>
    private void ReportError(string message)
    {
        SetAndRaise(ErrorTextProperty, ref _errorText, message);
        ShowControls();
    }

    /// <summary>
    /// 通知宿主处理全屏请求。
    /// </summary>
    private Task RequestFullScreen()
    {
        ShowControls();
        try { _fullScreenRequested?.Invoke(this, EventArgs.Empty); }
        catch (Exception exception)
        {
            ReportError(exception.Message);
            FluxMediaLog.Write(LogLevel.Warning, nameof(FluxPlayerControl), Player?.InstanceId ?? Guid.Empty,
                "Event=FullScreenRequestFailed", exception);
        }
        return Task.CompletedTask;
    }
}
