using LogConverge.Sharp;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluxMedia.Avalonia.Sample.ViewModels;
using FluxMedia.Playback;

namespace FluxMedia.Avalonia.Sample.Views;

/// <summary>
/// 承载播放器列表并处理全屏布局。
/// </summary>
public sealed partial class MainWindow : Window
{
    /// <summary>
    /// 管理播放器集合的主视图模型。
    /// </summary>
    private readonly MainWindowViewModel _viewModel;

    /// <summary>
    /// 当前全屏卡片。
    /// </summary>
    private PlayerItemView? _fullScreenItem;

    /// <summary>
    /// 进入全屏前的窗口状态。
    /// </summary>
    private WindowState _previousWindowState;

    /// <summary>
    /// 加载窗口并绑定播放器集合。
    /// </summary>
    public MainWindow()
    {
        InitializeComponent();
        _viewModel = new MainWindowViewModel();
        DataContext = _viewModel;
        _viewModel.Players.CollectionChanged += (_, _) =>
            Dispatcher.UIThread.Post(UpdatePlayerLayout, DispatcherPriority.Loaded);
        PlayerScroll.SizeChanged += (_, _) => UpdatePlayerLayout();
        Opened += (_, _) => UpdatePlayerLayout();
    }

    /// <summary>
    /// 移除指定播放器并处理释放异常。
    /// </summary>
    private async void OnRemoveRequested(object? sender, EventArgs e)
    {
        if (sender is not PlayerItemView { DataContext: Player player } item)
        {
            return;
        }
        if (ReferenceEquals(item, _fullScreenItem))
        {
            ExitFullScreen();
        }
        try
        {
            await _viewModel.RemovePlayerAsync(player);
        }
        catch (Exception exception)
        {
            Log.Error(nameof(MainWindow),
                $"SessionId={player.InstanceId} Event=PlayerRemovalFailed", exception);
        }
    }

    /// <summary>
    /// 切换当前播放器卡片的全屏布局。
    /// </summary>
    private void OnFullScreenRequested(object? sender, EventArgs e)
    {
        if (_fullScreenItem is not null)
        {
            ExitFullScreen();
            return;
        }
        if (sender is not PlayerItemView item)
        {
            return;
        }
        _fullScreenItem = item;
        _previousWindowState = WindowState;
        Header.IsVisible = false;
        PlayerList.Margin = new Thickness(0);
        PlayerScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
        foreach (var view in PlayerList.GetVisualDescendants().OfType<PlayerItemView>())
        {
            view.IsVisible = ReferenceEquals(view, item);
        }
        item.SetFullScreen(true);
        WindowState = WindowState.FullScreen;
        Dispatcher.UIThread.Post(UpdatePlayerLayout, DispatcherPriority.Loaded);
    }

    /// <summary>
    /// 按窗口宽度切换单双列，全屏只保留一列。
    /// </summary>
    private void UpdatePlayerLayout()
    {
        var panel = PlayerList.GetVisualDescendants().OfType<UniformGrid>().FirstOrDefault();
        if (panel is not null)
        {
            panel.Columns = _fullScreenItem is not null
                || _viewModel.Players.Count <= 1
                || PlayerScroll.Bounds.Width < 1200 ? 1 : 2;
        }

        UpdateFullScreenSize();
    }

    /// <summary>
    /// 调整全屏卡片尺寸以填满播放区域。
    /// </summary>
    private void UpdateFullScreenSize()
    {
        if (_fullScreenItem is null)
        {
            return;
        }
        _fullScreenItem.Width = Math.Max(1, PlayerScroll.Bounds.Width);
        _fullScreenItem.Height = Math.Max(1, PlayerScroll.Bounds.Height);
    }

    /// <summary>
    /// 退出全屏并恢复播放器列表。
    /// </summary>
    private void ExitFullScreen()
    {
        var item = _fullScreenItem;
        if (item is null)
        {
            return;
        }
        _fullScreenItem = null;
        item.SetFullScreen(false);
        foreach (var view in PlayerList.GetVisualDescendants().OfType<PlayerItemView>())
        {
            view.IsVisible = true;
        }
        Header.IsVisible = true;
        PlayerList.Margin = new Thickness(14);
        PlayerScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        WindowState = _previousWindowState;
        Dispatcher.UIThread.Post(UpdatePlayerLayout, DispatcherPriority.Loaded);
    }

    /// <summary>
    /// 响应未被子控件处理的 Escape 退出全屏。
    /// </summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || e.Key != Key.Escape || _fullScreenItem is null)
        {
            return;
        }
        ExitFullScreen();
        e.Handled = true;
    }

    /// <summary>
    /// 关闭窗口时释放全部播放器。
    /// </summary>
    protected override async void OnClosed(EventArgs e)
    {
        try
        {
            await _viewModel.DisposeAsync();
        }
        catch (Exception exception)
        {
            Log.Error(nameof(MainWindow),
                "Event=WindowCleanupFailed", exception);
        }
        finally
        {
            base.OnClosed(e);
        }
    }
}
