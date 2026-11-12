using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using FluxMedia.Avalonia.Sample.ViewModels;
using FluxMedia.Playback;

namespace FluxMedia.Avalonia.Sample.Views;

/// <summary>
/// 展示播放器及示例操作面板。
/// </summary>
public sealed partial class PlayerItemView : UserControl
{
    /// <summary>
    /// 示例自定义档位，覆盖 0.1～30 倍。
    /// </summary>
    private static readonly IReadOnlyList<double> DemoPlaybackSpeeds =
        Array.AsReadOnly(new[] { 0.1, 0.25, 0.6, 1.0, 1.3, 1.8, 2.5, 4.0, 8.0, 10.0, 12.0, 16.0, 20.0, 25.0, 30.0 });

    /// <summary>
    /// 当前卡片的视频和基础操作控件。
    /// </summary>
    private readonly FluxPlayerControl _playbackControl;

    /// <summary>
    /// 示例自定义控制栏开关。
    /// </summary>
    private readonly CheckBox _useCustomControls;

    /// <summary>
    /// 媒体输入和诊断面板。
    /// </summary>
    private readonly StackPanel _editor;

    /// <summary>
    /// 卡片外框。
    /// </summary>
    private readonly Border _card;

    /// <summary>
    /// 卡片标题栏。
    /// </summary>
    private readonly Grid _cardHeader;

    /// <summary>
    /// 卡片布局容器。
    /// </summary>
    private readonly Grid _cardLayout;

    /// <summary>
    /// 示例操作面板的视图模型。
    /// </summary>
    private PlayerItemViewModel? _panel;

    /// <summary>
    /// 卡片是否已挂载到可视树。
    /// </summary>
    private bool _isAttached;

    /// <summary>
    /// 加载播放器卡片界面。
    /// </summary>
    public PlayerItemView()
    {
        AvaloniaXamlLoader.Load(this);
        _playbackControl = this.FindControl<FluxPlayerControl>("PlaybackControl")
            ?? throw new InvalidOperationException("Missing PlaybackControl.");
        _useCustomControls = this.FindControl<CheckBox>("UseCustomControls")
            ?? throw new InvalidOperationException("Missing UseCustomControls.");
        _editor = this.FindControl<StackPanel>("Editor")
            ?? throw new InvalidOperationException("Missing Editor.");
        _card = this.FindControl<Border>("Card")
            ?? throw new InvalidOperationException("Missing Card.");
        _cardHeader = this.FindControl<Grid>("CardHeader")
            ?? throw new InvalidOperationException("Missing CardHeader.");
        _cardLayout = this.FindControl<Grid>("CardLayout")
            ?? throw new InvalidOperationException("Missing CardLayout.");
        var removeButton = this.FindControl<Button>("RemoveButton")
            ?? throw new InvalidOperationException("Missing RemoveButton.");
        var useCustomSpeeds = this.FindControl<CheckBox>("UseCustomSpeeds")
            ?? throw new InvalidOperationException("Missing UseCustomSpeeds.");

        removeButton.Click += OnRemoveClick;
        _useCustomControls.Click += OnControlBarStyleChanged;
        useCustomSpeeds.Click += OnSpeedPresetsChanged;
        _playbackControl.PlaybackSpeeds = DemoPlaybackSpeeds;
        _playbackControl.FullScreenRequested += OnFullScreenRequest;
        SizeChanged += (_, _) => UpdateVideoSize();
    }

    /// <summary>
    /// 请求宿主移除并释放当前播放器。
    /// </summary>
    public event EventHandler? RemoveRequested;

    /// <summary>
    /// 请求宿主切换当前卡片的全屏状态。
    /// </summary>
    public event EventHandler? FullScreenRequested;

    /// <summary>
    /// 挂载时关联示例操作面板。
    /// </summary>
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _isAttached = true;
        AttachPanel();
    }

    /// <summary>
    /// 卸载时释放示例面板，保留播放器。
    /// </summary>
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _isAttached = false;
        _panel?.Dispose();
        _panel = null;
        _editor.DataContext = null;
        base.OnDetachedFromVisualTree(e);
    }

    /// <summary>
    /// 播放器变化后重新关联操作面板。
    /// </summary>
    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_isAttached)
        {
            AttachPanel();
        }
    }

    /// <summary>
    /// 为当前播放器创建示例操作面板。
    /// </summary>
    private void AttachPanel()
    {
        _panel?.Dispose();
        _panel = DataContext is Player player ? new PlayerItemViewModel(player, BrowseMediaAsync) : null;
        _editor.DataContext = _panel;
    }

    /// <summary>
    /// 打开媒体文件选择窗口。
    /// </summary>
    private async Task<string?> BrowseMediaAsync()
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null)
        {
            return null;
        }
        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            AllowMultiple = false,
            Title = "选择媒体文件"
        });
        return files.FirstOrDefault()?.TryGetLocalPath();
    }

    /// <summary>
    /// 转发播放器移除请求。
    /// </summary>
    private void OnRemoveClick(object? sender, RoutedEventArgs e) => RemoveRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>
    /// 转发全屏切换请求。
    /// </summary>
    private void OnFullScreenRequest(object? sender, EventArgs e) => FullScreenRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>
    /// 切换默认或示例自定义控制栏。
    /// </summary>
    private void OnControlBarStyleChanged(object? sender, RoutedEventArgs e)
    {
        _playbackControl.ControlBarTemplate = _useCustomControls.IsChecked == true
            ? (IDataTemplate)Resources["CompactControlBar"]!
            : (IDataTemplate)Resources["StandardControlBar"]!;
    }

    /// <summary>
    /// 示例：切换使用方档位与播放器默认档位。
    /// </summary>
    private void OnSpeedPresetsChanged(object? sender, RoutedEventArgs e)
    {
        if (sender is CheckBox { IsChecked: true })
        {
            _playbackControl.PlaybackSpeeds = DemoPlaybackSpeeds;
        }
        else
        {
            _playbackControl.ClearValue(FluxPlayerControl.PlaybackSpeedsProperty);
        }
    }

    /// <summary>
    /// 切换卡片布局，复用原视频控件。
    /// </summary>
    public void SetFullScreen(bool fullScreen)
    {
        _cardHeader.IsVisible = !fullScreen;
        _editor.IsVisible = !fullScreen;
        _card.Margin = fullScreen ? new Thickness(0) : new Thickness(7);
        _card.Padding = new Thickness(0);
        _card.BorderThickness = fullScreen ? new Thickness(0) : new Thickness(1);
        _card.CornerRadius = fullScreen ? new CornerRadius(0) : new CornerRadius(10);
        _cardLayout.RowDefinitions[1].Height = fullScreen ? new GridLength(1, GridUnitType.Star) : new GridLength(320);
        if (!fullScreen)
        {
            Width = double.NaN;
            Height = double.NaN;
            UpdateVideoSize();
        }
        _playbackControl.ShowControls();
    }

    /// <summary>
    /// 根据卡片宽度保持画面为 16:9，全屏时由宿主布局控制。
    /// </summary>
    private void UpdateVideoSize()
    {
        if (!_editor.IsVisible || Bounds.Width <= 0)
        {
            return;
        }

        var videoWidth = Math.Max(1, Bounds.Width - 16);
        _cardLayout.RowDefinitions[1].Height = new GridLength(videoWidth * 9 / 16);
    }
}
