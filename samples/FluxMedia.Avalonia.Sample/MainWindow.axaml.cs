using Avalonia.Controls;

namespace FluxMedia.Avalonia.Sample;

/// <summary>
/// 播放器示例的主窗口。
/// </summary>
public sealed partial class MainWindow : Window
{
    /// <summary>
    /// 视图模型。
    /// </summary>
    private readonly MainWindowViewModel _viewModel;

    /// <summary>
    /// 初始化主入口窗口所需的状态和资源。
    /// </summary>
    public MainWindow()
    {
        InitializeComponent();
        _viewModel = new MainWindowViewModel(this);
        DataContext = _viewModel;
    }

    /// <summary>
    /// 窗口关闭后清理持有的播放器资源。
    /// </summary>
    protected override async void OnClosed(EventArgs e)
    {
        await _viewModel.DisposeAsync();
        base.OnClosed(e);
    }
}
