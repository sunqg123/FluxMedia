using Avalonia;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Media;
using Avalonia.Threading;
using FluxMedia.Avalonia.Internal;
using FluxMedia.Playback;
using System.Windows.Input;

namespace FluxMedia.Avalonia;

/// <summary>
/// 带基础操作和自定义覆盖层的播放器控件。
/// </summary>
public sealed partial class FluxPlayerControl : TemplatedControl
{
    /// <summary>
    /// 播放器绑定属性。
    /// </summary>
    public static readonly StyledProperty<Player?> PlayerProperty =
        AvaloniaProperty.Register<FluxPlayerControl, Player?>(nameof(Player));

    /// <summary>
    /// 使用方提供的倍速档位，未设置时采用播放器默认列表。
    /// </summary>
    public static readonly StyledProperty<IReadOnlyList<double>> PlaybackSpeedsProperty =
        AvaloniaProperty.Register<FluxPlayerControl, IReadOnlyList<double>>(
            nameof(PlaybackSpeeds), global::FluxMedia.Playback.Player.DefaultSpeeds,
            coerce: static (_, speeds) => speeds ?? global::FluxMedia.Playback.Player.DefaultSpeeds);

    /// <summary>
    /// 画面缩放方式的绑定属性。
    /// </summary>
    public static readonly StyledProperty<Stretch> StretchProperty =
        AvaloniaProperty.Register<FluxPlayerControl, Stretch>(nameof(Stretch), Stretch.Uniform);

    /// <summary>
    /// 控制栏模板绑定属性。
    /// </summary>
    public static readonly StyledProperty<IDataTemplate?> ControlBarTemplateProperty =
        AvaloniaProperty.Register<FluxPlayerControl, IDataTemplate?>(nameof(ControlBarTemplate));

    /// <summary>
    /// 覆盖内容绑定属性。
    /// </summary>
    public static readonly StyledProperty<object?> OverlayContentProperty =
        AvaloniaProperty.Register<FluxPlayerControl, object?>(nameof(OverlayContent));

    /// <summary>
    /// 覆盖内容模板的绑定属性。
    /// </summary>
    public static readonly StyledProperty<IDataTemplate?> OverlayContentTemplateProperty =
        AvaloniaProperty.Register<FluxPlayerControl, IDataTemplate?>(nameof(OverlayContentTemplate));

    /// <summary>
    /// 自动隐藏开关属性。
    /// </summary>
    public static readonly StyledProperty<bool> AutoHideControlsProperty =
        AvaloniaProperty.Register<FluxPlayerControl, bool>(nameof(AutoHideControls), true);

    /// <summary>
    /// 自动隐藏延时的绑定属性。
    /// </summary>
    public static readonly StyledProperty<TimeSpan> AutoHideDelayProperty =
        AvaloniaProperty.Register<FluxPlayerControl, TimeSpan>(nameof(AutoHideDelay), TimeSpan.FromSeconds(3),
            validate: value => value >= TimeSpan.Zero && value <= TimeSpan.FromDays(1));

    /// <summary>
    /// 控制栏可见状态属性。
    /// </summary>
    public static readonly DirectProperty<FluxPlayerControl, bool> AreControlsVisibleProperty =
        AvaloniaProperty.RegisterDirect<FluxPlayerControl, bool>(nameof(AreControlsVisible), control => control.AreControlsVisible);

    /// <summary>
    /// 操作错误文本的绑定属性。
    /// </summary>
    public static readonly DirectProperty<FluxPlayerControl, string> ErrorTextProperty =
        AvaloniaProperty.RegisterDirect<FluxPlayerControl, string>(nameof(ErrorText), control => control.ErrorText);

    /// <summary>
    /// 命令执行状态的绑定属性。
    /// </summary>
    public static readonly DirectProperty<FluxPlayerControl, bool> IsBusyProperty =
        AvaloniaProperty.RegisterDirect<FluxPlayerControl, bool>(nameof(IsBusy), control => control.IsBusy);

    /// <summary>
    /// 倍速尚未应用的绑定状态。
    /// </summary>
    public static readonly DirectProperty<FluxPlayerControl, bool> IsSpeedChangePendingProperty =
        AvaloniaProperty.RegisterDirect<FluxPlayerControl, bool>(nameof(IsSpeedChangePending), control => control.IsSpeedChangePending);

    /// <summary>
    /// 控制栏自动隐藏计时器。
    /// </summary>
    private readonly DispatcherTimer _autoHideTimer = new();

    /// <summary>
    /// 模板中的视频显示层。
    /// </summary>
    private FluxPlayerView? _videoView;

    /// <summary>
    /// 模板中的控制栏容器。
    /// </summary>
    private ContentPresenter? _controlsHost;

    /// <summary>
    /// 已订阅状态通知的播放器。
    /// </summary>
    private Player? _subscribedPlayer;

    /// <summary>
    /// 控件是否已挂载到可视树。
    /// </summary>
    private bool _isAttached;

    /// <summary>
    /// 订阅代次，用于忽略旧回调。
    /// </summary>
    private long _subscriptionVersion;

    /// <summary>
    /// 控制栏可见状态。
    /// </summary>
    private bool _areControlsVisible = true;

    /// <summary>
    /// 最近的操作错误文本。
    /// </summary>
    private string _errorText = string.Empty;

    /// <summary>
    /// 是否正在执行界面命令。
    /// </summary>
    private bool _isBusy;

    /// <summary>
    /// 当前播放器是否仍在等待换速。
    /// </summary>
    private bool _isSpeedChangePending;

    /// <summary>
    /// 播放或暂停命令。
    /// </summary>
    private readonly PlaybackControlCommand _playPauseCommand;

    /// <summary>
    /// 停止命令。
    /// </summary>
    private readonly PlaybackControlCommand _stopCommand;

    /// <summary>
    /// 后退十秒的命令。
    /// </summary>
    private readonly PlaybackControlCommand _seekBackwardCommand;

    /// <summary>
    /// 快进十秒的命令。
    /// </summary>
    private readonly PlaybackControlCommand _seekForwardCommand;

    /// <summary>
    /// 向宿主请求全屏的命令。
    /// </summary>
    private readonly PlaybackControlCommand _fullScreenCommand;

    /// <summary>
    /// 全屏请求的订阅者。
    /// </summary>
    private EventHandler? _fullScreenRequested;

    /// <summary>
    /// 注册默认容器模板和控件属性默认值。
    /// </summary>
    static FluxPlayerControl()
    {
        FocusableProperty.OverrideDefaultValue<FluxPlayerControl>(true);
        BackgroundProperty.OverrideDefaultValue<FluxPlayerControl>(Brushes.Black);
        TemplateProperty.OverrideDefaultValue<FluxPlayerControl>(
            new FuncControlTemplate<FluxPlayerControl>(CreateDefaultTemplate));
    }

    /// <summary>
    /// 初始化播放命令和输入事件。
    /// </summary>
    public FluxPlayerControl()
    {
        _playPauseCommand = new PlaybackControlCommand(() => ExecutePlaybackAsync(
            player => player.IsPlaying ? player.PauseAsync() : player.PlayAsync()), CanPlayPause);
        _stopCommand = new PlaybackControlCommand(
            () => ExecutePlaybackAsync(async player =>
            {
                await player.StopAsync();
                return PlaybackResult.Success();
            }),
            CanStop);
        _seekBackwardCommand = new PlaybackControlCommand(() => ExecutePlaybackAsync(
            player => player.SeekByAsync(TimeSpan.FromSeconds(-10))), CanSeek);
        _seekForwardCommand = new PlaybackControlCommand(() => ExecutePlaybackAsync(
            player => player.SeekByAsync(TimeSpan.FromSeconds(10))), CanSeek);
        _fullScreenCommand = new PlaybackControlCommand(RequestFullScreen, () => _fullScreenRequested is not null);
        _autoHideTimer.Tick += OnAutoHideTick;
        InitializeInputHandlers();
    }

    /// <summary>
    /// 播放器实例，由调用方负责创建和释放。
    /// </summary>
    public Player? Player
    {
        get => GetValue(PlayerProperty);
        set => SetValue(PlayerProperty, value);
    }

    /// <summary>
    /// 画面缩放方式。
    /// </summary>
    public Stretch Stretch
    {
        get => GetValue(StretchProperty);
        set => SetValue(StretchProperty, value);
    }

    /// <summary>
    /// 自定义控制栏模板，数据上下文为 Player。
    /// </summary>
    public IDataTemplate? ControlBarTemplate
    {
        get => GetValue(ControlBarTemplateProperty);
        set => SetValue(ControlBarTemplateProperty, value);
    }

    /// <summary>
    /// 视频上的业务内容，不随控制栏隐藏。
    /// </summary>
    public object? OverlayContent
    {
        get => GetValue(OverlayContentProperty);
        set => SetValue(OverlayContentProperty, value);
    }

    /// <summary>
    /// 业务内容模板，数据来自 OverlayContent。
    /// </summary>
    public IDataTemplate? OverlayContentTemplate
    {
        get => GetValue(OverlayContentTemplateProperty);
        set => SetValue(OverlayContentTemplateProperty, value);
    }

    /// <summary>
    /// 是否在播放闲置时自动隐藏控制栏。
    /// </summary>
    public bool AutoHideControls
    {
        get => GetValue(AutoHideControlsProperty);
        set => SetValue(AutoHideControlsProperty, value);
    }

    /// <summary>
    /// 自动隐藏延时；零表示常显。
    /// </summary>
    public TimeSpan AutoHideDelay
    {
        get => GetValue(AutoHideDelayProperty);
        set => SetValue(AutoHideDelayProperty, value);
    }

    /// <summary>
    /// 控制栏是否可见。
    /// </summary>
    public bool AreControlsVisible => _areControlsVisible;

    /// <summary>
    /// 最近的操作错误说明。
    /// </summary>
    public string ErrorText => _errorText;

    /// <summary>
    /// 是否正在执行播放命令。
    /// </summary>
    public bool IsBusy => _isBusy;

    /// <summary>
    /// 换速已接受但尚未由处理链确认。
    /// </summary>
    public bool IsSpeedChangePending => _isSpeedChangePending;

    /// <summary>
    /// 可绑定的倍速档位；清除设置后恢复默认列表。
    /// </summary>
    public IReadOnlyList<double> PlaybackSpeeds
    {
        get => GetValue(PlaybackSpeedsProperty);
        set => SetValue(PlaybackSpeedsProperty, value);
    }

    /// <summary>
    /// 播放或暂停当前播放器。
    /// </summary>
    public ICommand PlayPauseCommand => _playPauseCommand;

    /// <summary>
    /// 停止当前播放器。
    /// </summary>
    public ICommand StopCommand => _stopCommand;

    /// <summary>
    /// 后退十秒，直播禁用。
    /// </summary>
    public ICommand SeekBackwardCommand => _seekBackwardCommand;

    /// <summary>
    /// 快进十秒，直播禁用。
    /// </summary>
    public ICommand SeekForwardCommand => _seekForwardCommand;

    /// <summary>
    /// 请求宿主切换全屏，无订阅者时禁用。
    /// </summary>
    public ICommand FullScreenCommand => _fullScreenCommand;

    /// <summary>
    /// 请求宿主切换窗口或页面的全屏状态。
    /// </summary>
    public event EventHandler? FullScreenRequested
    {
        add
        {
            _fullScreenRequested += value;
            _fullScreenCommand.RaiseCanExecuteChanged();
        }
        remove
        {
            _fullScreenRequested -= value;
            _fullScreenCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>
    /// 显示控制栏并记录操作时间。
    /// </summary>
    public void ShowControls()
    {
        SetAndRaise(AreControlsVisibleProperty, ref _areControlsVisible, true);
        RecordActivity();
    }

    /// <summary>
    /// 条件允许时隐藏控制栏。
    /// </summary>
    public void HideControls()
    {
        if (!CanHideControls() || IsInteracting())
            return;
        _autoHideTimer.Stop();
        // 隐藏获得焦点的按钮后，键盘仍可在播放器上唤回控制栏。
        if (_controlsHost?.IsKeyboardFocusWithin == true) Focus();
        SetAndRaise(AreControlsVisibleProperty, ref _areControlsVisible, false);
    }
}
