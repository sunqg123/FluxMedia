using FluxMedia.Core.Configuration;
using FluxMedia.Core.Logging;
using System.Windows.Input;

namespace FluxMedia.Avalonia.Internal;

/// <summary>
/// 防止重复执行的异步界面命令。
/// </summary>
internal sealed class PlaybackControlCommand : ICommand
{
    /// <summary>
    /// 命令执行回调。
    /// </summary>
    private readonly Func<Task> _execute;

    /// <summary>
    /// 命令可用条件。
    /// </summary>
    private readonly Func<bool> _canExecute;

    /// <summary>
    /// 命令是否正在执行。
    /// </summary>
    private bool _executing;

    /// <summary>
    /// 设置执行回调和可用条件。
    /// </summary>
    public PlaybackControlCommand(Func<Task> execute, Func<bool> canExecute)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    /// <summary>
    /// 命令可用状态变更通知。
    /// </summary>
    public event EventHandler? CanExecuteChanged;

    /// <summary>
    /// 检查可用条件和重复执行状态。
    /// </summary>
    public bool CanExecute(object? parameter) => !_executing && _canExecute();

    /// <summary>
    /// 执行命令并记录未处理的异常。
    /// </summary>
    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter))
        {
            return;
        }
        _executing = true;
        RaiseCanExecuteChanged();
        try
        {
            await _execute();
        }
        catch (Exception exception)
        {
            FluxMediaLog.Write(
                LogLevel.Error,
                nameof(PlaybackControlCommand),
                Guid.Empty,
                "Event=ControlCommandFailed",
                exception);
        }
        finally
        {
            _executing = false;
            RaiseCanExecuteChanged();
        }
    }

    /// <summary>
    /// 通知界面刷新命令状态。
    /// </summary>
    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
