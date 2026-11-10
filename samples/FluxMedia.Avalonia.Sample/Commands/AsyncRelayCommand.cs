using System.Windows.Input;

namespace FluxMedia.Avalonia.Sample.Commands;

/// <summary>
/// 执行异步界面命令并控制可执行状态。
/// </summary>
public sealed class AsyncRelayCommand : ICommand
{
    /// <summary>
    /// 命令需要执行的操作。
    /// </summary>
    private readonly Func<Task> _execute;

    /// <summary>
    /// 是否执行中。
    /// </summary>
    private bool _executing;

    /// <summary>
    /// 初始化异步转发命令所需的状态和资源。
    /// </summary>
    public AsyncRelayCommand(Func<Task> execute)
    {
        _execute = execute;
    }

    /// <summary>
    /// 命令可执行状态变化时发出的通知。
    /// </summary>
    public event EventHandler? CanExecuteChanged;

    /// <summary>
    /// 判断当前命令是否允许执行。
    /// </summary>
    public bool CanExecute(object? parameter) => !_executing;

    /// <summary>
    /// 执行绑定的界面命令。
    /// </summary>
    public async void Execute(object? parameter)
    {
        if (_executing)
        {
            return;
        }

        _executing = true;
        CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        try
        {
            await _execute();
        }
        finally
        {
            _executing = false;
            CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
