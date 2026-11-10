using Avalonia.Controls;
using Avalonia.Platform.Storage;
using FluxMedia.Avalonia.Sample.ViewModels;

namespace FluxMedia.Avalonia.Sample.Views;

/// <summary>
/// 播放器示例的主窗口。
/// </summary>
public sealed partial class MainWindow : Window
{
    /// <summary>
    /// 窗口持有的视图模型。
    /// </summary>
    private readonly MainWindowViewModel _viewModel;

    /// <summary>
    /// 加载窗口并连接视图模型。
    /// </summary>
    public MainWindow()
    {
        InitializeComponent();
        _viewModel = new MainWindowViewModel(BrowseMediaAsync);
        DataContext = _viewModel;
    }

    /// <summary>
    /// 打开文件选择窗口并返回媒体路径。
    /// </summary>
    private async Task<string?> BrowseMediaAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            AllowMultiple = false,
            Title = "Choose media file"
        });
        return files.FirstOrDefault()?.Path.LocalPath;
    }

    /// <summary>
    /// 窗口关闭后释放播放器。
    /// </summary>
    protected override async void OnClosed(EventArgs e)
    {
        await _viewModel.DisposeAsync();
        base.OnClosed(e);
    }
}
