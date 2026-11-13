using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using System.Diagnostics;

namespace FluxMedia.Avalonia;

public sealed partial class FluxPlayerControl
{
    /// <summary>
    /// 模板中的业务覆盖层容器。
    /// </summary>
    private ContentPresenter? _overlayHost;

    /// <summary>
    /// 当前交互中的指针。
    /// </summary>
    private IPointer? _pressedPointer;

    /// <summary>
    /// 指针是否在画面区域按下。
    /// </summary>
    private bool _pressedOnVideo;

    /// <summary>
    /// 按下时控制栏是否可见。
    /// </summary>
    private bool _visibleOnPress;

    /// <summary>
    /// 指针按下位置，用于区分点击和拖动。
    /// </summary>
    private Point _pressPosition;

    /// <summary>
    /// 上次鼠标位置，用于过滤重复移动。
    /// </summary>
    private Point? _lastPointerPosition;

    /// <summary>
    /// 最近一次用户操作的单调时钟刻度。
    /// </summary>
    private long _lastActivityTimestamp = Stopwatch.GetTimestamp();

    /// <summary>
    /// 注册输入预览，避免操作期间隐藏控制栏。
    /// </summary>
    private void InitializeInputHandlers()
    {
        AddHandler(PointerPressedEvent, OnPreviewPointerPressed, RoutingStrategies.Tunnel, true);
        AddHandler(PointerReleasedEvent, OnPreviewPointerReleased, RoutingStrategies.Tunnel, true);
        AddHandler(
            PointerCaptureLostEvent,
            OnControlPointerCaptureLost,
            RoutingStrategies.Direct | RoutingStrategies.Bubble,
            true);
        AddHandler(PointerMovedEvent, OnControlPointerMoved, RoutingStrategies.Tunnel, true);
        AddHandler(KeyDownEvent, OnControlKeyDown, RoutingStrategies.Tunnel, true);
    }

    /// <summary>
    /// 获取视频、控制栏和覆盖层模板部件。
    /// </summary>
    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);
        _templateRoot = e.NameScope.Find<Grid>("PART_Root");
        _videoView = e.NameScope.Find<FluxPlayerView>("PART_VideoView");
        _controlsHost = e.NameScope.Find<ContentPresenter>("PART_ControlsHost");
        _overlayHost = e.NameScope.Find<ContentPresenter>("PART_OverlayHost");
        ApplyTemplateParts();
        EnsureAutoHideTimer();
    }

    /// <summary>
    /// 挂载时订阅播放器状态。
    /// </summary>
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _isAttached = true;
        SubscribePlayer();
    }

    /// <summary>
    /// 卸载时清理计时和订阅，保留播放器。
    /// </summary>
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _isAttached = false;
        _autoHideTimer.Stop();
        if (_pressedPointer?.Captured == this)
        {
            _pressedPointer.Capture(null);
        }
        _pressedPointer = null;
        _lastPointerPosition = null;
        UnsubscribePlayer();
        RefreshCommands();
        base.OnDetachedFromVisualTree(e);
    }

    /// <summary>
    /// 响应播放器和自动隐藏设置变更。
    /// </summary>
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == PlayerProperty)
        {
            SubscribePlayer();
        }
        else if (change.Property == AutoHideControlsProperty || change.Property == AutoHideDelayProperty)
        {
            _autoHideTimer.Stop();
            ShowControls();
        }
        ApplyTemplateParts(change.Property);
        if (change.Property == ControlBarTemplateProperty)
        {
            EnsureAutoHideTimer();
        }
    }

    /// <summary>
    /// 将已有属性同步到新模板；属性变更时只更新对应部件。
    /// </summary>
    private void ApplyTemplateParts(AvaloniaProperty? property = null)
    {
        if (property is null || property == PlayerProperty)
        {
            _videoView?.SetCurrentValue(FluxPlayerView.PlayerProperty, Player);
        }
        if (property is null || property == StretchProperty)
        {
            _videoView?.SetCurrentValue(FluxPlayerView.StretchProperty, Stretch);
        }
        if (property is null || property == BackgroundProperty)
        {
            _templateRoot?.SetCurrentValue(Panel.BackgroundProperty, Background);
        }
        if (property is null || property == OverlayContentProperty)
        {
            _overlayHost?.SetCurrentValue(ContentPresenter.ContentProperty, OverlayContent);
        }
        if (property is null || property == OverlayContentTemplateProperty)
        {
            _overlayHost?.SetCurrentValue(ContentPresenter.ContentTemplateProperty, OverlayContentTemplate);
        }
        if (property is null || property == PlayerProperty || property == ControlBarTemplateProperty)
        {
            UpdateControlBar();
        }
        if (property is null || property == AreControlsVisibleProperty)
        {
            _controlsHost?.SetCurrentValue(IsVisibleProperty, AreControlsVisible);
        }
    }

    /// <summary>
    /// 关联使用方控制栏模板；未提供模板时不显示内容。
    /// </summary>
    private void UpdateControlBar()
    {
        if (_controlsHost is null)
        {
            return;
        }

        _controlsHost.SetCurrentValue(ContentPresenter.ContentTemplateProperty, ControlBarTemplate);
        _controlsHost.SetCurrentValue(ContentPresenter.ContentProperty, ControlBarTemplate is null ? null : Presentation);
    }

    /// <summary>
    /// 检查播放状态是否允许隐藏控制栏。
    /// </summary>
    private bool CanHideControls() => _isAttached
        && !IsBusy
        && !IsSpeedChangePending
        && string.IsNullOrEmpty(ErrorText)
        && Presentation is { IsDisposed: false, IsPlaying: true, IsSeeking: false };

    /// <summary>
    /// 检查自动隐藏开关、延时和播放状态。
    /// </summary>
    private bool CanAutoHide() => ControlBarTemplate is not null
        && AutoHideControls
        && AutoHideDelay > TimeSpan.Zero
        && CanHideControls();

    /// <summary>
    /// 检查指针和菜单是否仍在交互，焦点停留不代表持续操作。
    /// </summary>
    private bool IsInteracting()
    {
        var pointerCaptured = _pressedPointer?.Captured is Visual target
            && (ReferenceEquals(target, this) || this.IsVisualAncestorOf(target));
        var pointerOverControls = _controlsHost?.IsPointerOver == true || _overlayHost?.IsPointerOver == true;
        if (pointerCaptured || pointerOverControls)
        {
            return true;
        }

        return this.GetVisualDescendants().OfType<Control>().Any(control =>
            control is ComboBox { IsDropDownOpen: true }
                or Popup { IsOpen: true }
                or Button { Flyout: { IsOpen: true } }
            || control.ContextMenu?.IsOpen == true
            || control.ContextFlyout?.IsOpen == true);
    }

    /// <summary>
    /// 记录操作时间，已运行的计时器无需重新启动。
    /// </summary>
    private void RecordActivity()
    {
        _lastActivityTimestamp = Stopwatch.GetTimestamp();
        EnsureAutoHideTimer();
    }

    /// <summary>
    /// 仅在需要时启停自动隐藏计时器。
    /// </summary>
    private void EnsureAutoHideTimer()
    {
        if (!CanAutoHide() || !AreControlsVisible)
        {
            if (_autoHideTimer.IsEnabled)
                _autoHideTimer.Stop();
            return;
        }
        if (_autoHideTimer.IsEnabled)
            return;
        _autoHideTimer.Interval = AutoHideDelay;
        _autoHideTimer.Start();
    }

    /// <summary>
    /// 闲置时隐藏，交互中继续等待。
    /// </summary>
    private void OnAutoHideTick(object? sender, EventArgs e)
    {
        _autoHideTimer.Stop();
        if (!CanAutoHide() || !AreControlsVisible)
        {
            return;
        }
        var elapsed = TimeSpan.FromSeconds((Stopwatch.GetTimestamp() - _lastActivityTimestamp) / (double)Stopwatch.Frequency);
        var remaining = AutoHideDelay - elapsed;
        if (remaining > TimeSpan.Zero)
        {
            _autoHideTimer.Interval = remaining;
            _autoHideTimer.Start();
        }
        else if (IsInteracting())
        {
            EnsureAutoHideTimer();
        }
        else
        {
            HideControls();
        }
    }

    /// <summary>
    /// 记录按下位置，仅捕获画面区域的指针。
    /// </summary>
    private void OnPreviewPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Pointer.Type == PointerType.Mouse && !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }
        _pressedPointer = e.Pointer;
        _pressPosition = e.GetPosition(this);
        _pressedOnVideo = e.Source is Visual source && _videoView is not null
            && (ReferenceEquals(source, _videoView) || _videoView.IsVisualAncestorOf(source));
        _visibleOnPress = AreControlsVisible;
        if (_pressedOnVideo)
        {
            Focus();
            e.Pointer.Capture(this);
        }
        if (!_pressedOnVideo || !_visibleOnPress)
        {
            ShowControls();
        }
        else
        {
            RecordActivity();
        }
    }

    /// <summary>
    /// 画面点击切换控制栏，拖动不触发切换。
    /// </summary>
    private void OnPreviewPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!ReferenceEquals(e.Pointer, _pressedPointer))
        {
            return;
        }
        var offset = e.GetPosition(this) - _pressPosition;
        var toggle = _pressedOnVideo && offset.X * offset.X + offset.Y * offset.Y <= 36;
        _pressedPointer = null;
        if (e.Pointer.Captured == this)
        {
            e.Pointer.Capture(null);
        }
        if (toggle && _visibleOnPress)
        {
            HideControls();
            RecordActivity();
        }
        else
        {
            ShowControls();
        }
    }

    /// <summary>
    /// 指针捕获结束后恢复隐藏计时。
    /// </summary>
    private void OnControlPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        if (e.Pointer.Captured is Visual target && this.IsVisualAncestorOf(target))
        {
            return;
        }
        if (!ReferenceEquals(e.Pointer, _pressedPointer))
        {
            return;
        }
        _pressedPointer = null;
        RecordActivity();
    }

    /// <summary>
    /// 鼠标移动时显示控制栏。
    /// </summary>
    private void OnControlPointerMoved(object? sender, PointerEventArgs e)
    {
        if (e.Pointer.Type != PointerType.Mouse)
        {
            return;
        }
        var position = e.GetPosition(this);
        if (_lastPointerPosition == position)
        {
            return;
        }
        _lastPointerPosition = position;
        ShowControls();
    }

    /// <summary>
    /// 鼠标进入时显示控制栏。
    /// </summary>
    protected override void OnPointerEntered(PointerEventArgs e)
    {
        base.OnPointerEntered(e);
        if (e.Pointer.Type == PointerType.Mouse)
        {
            ShowControls();
        }
    }

    /// <summary>
    /// 键盘操作时显示控制栏，画面焦点下空格切换播放。
    /// </summary>
    private void OnControlKeyDown(object? sender, KeyEventArgs e)
    {
        ShowControls();
        if (e.Key != Key.Space || !ReferenceEquals(e.Source, this) || !PlayPauseCommand.CanExecute(null))
        {
            return;
        }
        PlayPauseCommand.Execute(null);
        e.Handled = true;
    }
}
