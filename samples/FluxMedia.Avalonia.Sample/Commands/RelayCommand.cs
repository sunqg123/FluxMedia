using System.Windows.Input;

namespace FluxMedia.Avalonia.Sample.Commands;

/// <summary>
/// 将界面操作转发为同步命令。
/// </summary>
public sealed class RelayCommand : ICommand
{
    /// <summary>
    /// 命令需要执行的操作。
    /// </summary>
    private readonly Action _execute;

    /// <summary>
    /// 初始化转发命令所需的状态和资源。
    /// </summary>
    public RelayCommand(Action execute)
    {
        _execute = execute;
    }

    /// <summary>
    /// 命令可执行状态变化时发出的通知。
    /// </summary>
    public event EventHandler? CanExecuteChanged
    {
        add
        {
        }
        remove
        {
        }
    }

    /// <summary>
    /// 判断当前命令是否允许执行。
    /// </summary>
    public bool CanExecute(object? parameter) => true;

    /// <summary>
    /// 执行绑定的界面命令。
    /// </summary>
    public void Execute(object? parameter) => _execute();
}
