using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using FluxMedia.Core.Models;
using FluxMedia.Playback;

namespace FluxMedia.Avalonia.Sample;

/// <summary>
/// 管理示例窗口中的播放器和界面命令。
/// </summary>
public sealed class MainWindowViewModel : NotifyObject, IAsyncDisposable
{
    /// <summary>
    /// 窗口。
    /// </summary>
    private readonly Window _window;

    /// <summary>
    /// 分配给下一个播放器的递增标识。
    /// </summary>
    private int _nextPlayerId = 1;

    /// <summary>
    /// 初始化主入口窗口视图模型所需的状态和资源。
    /// </summary>
    public MainWindowViewModel(Window window)
    {
        _window = window;
        Players.CollectionChanged += (_, _) => OnPropertyChanged(nameof(PlayerCountText));
        AddPlayerCommand = new RelayCommand(AddPlayer);
        AddPlayer();
        AddPlayer();
    }

    /// <summary>
    /// 当前窗口管理的播放器集合。
    /// </summary>
    public ObservableCollection<PlayerItemViewModel> Players { get; } = new();

    /// <summary>
    /// 添加播放器命令。
    /// </summary>
    public ICommand AddPlayerCommand { get; }

    /// <summary>
    /// 播放器数量文本。
    /// </summary>
    public string PlayerCountText => $"{Players.Count} player(s)";

    /// <summary>
    /// 释放当前对象持有的资源。
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        foreach (var player in Players.ToArray())
        {
            await player.DisposeAsync();
        }

        Players.Clear();
    }

    /// <summary>
    /// 添加一个播放器列表项。
    /// </summary>
    private void AddPlayer()
    {
        var item = new PlayerItemViewModel(_nextPlayerId++, BrowseAsync, RemoveAsync);
        Players.Add(item);
        UpdateRemoveState();
    }

    /// <summary>
    /// 释放并移除指定播放器。
    /// </summary>
    private async Task RemoveAsync(PlayerItemViewModel item)
    {
        if (!Players.Remove(item)) return;
        await item.DisposeAsync();
        UpdateRemoveState();
    }

    /// <summary>
    /// 选择需要打开的本地媒体文件。
    /// </summary>
    private async Task<string?> BrowseAsync()
    {
        var files = await _window.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            AllowMultiple = false,
            Title = "Choose media file"
        });
        return files.FirstOrDefault()?.Path.LocalPath;
    }

    /// <summary>
    /// 根据播放器数量更新移除操作的可用性。
    /// </summary>
    private void UpdateRemoveState()
    {
        foreach (var player in Players)
        {
            player.CanRemove = Players.Count > 1;
        }
    }
}

/// <summary>
/// 提供播放器列表项的状态和操作命令。
/// </summary>
public sealed class PlayerItemViewModel : NotifyObject, IAsyncDisposable
{
    /// <summary>
    /// 选择媒体文件的回调。
    /// </summary>
    private readonly Func<Task<string?>> _browse;

    /// <summary>
    /// 移除当前播放器的回调。
    /// </summary>
    private readonly Func<PlayerItemViewModel, Task> _remove;

    /// <summary>
    /// 定期刷新播放诊断信息的界面定时器。
    /// </summary>
    private readonly DispatcherTimer _diagnosticTimer;

    /// <summary>
    /// 待打开的媒体路径或网络地址文本。
    /// </summary>
    private string _sourceText = string.Empty;

    /// <summary>
    /// 状态文本。
    /// </summary>
    private string _stateText = "Created";

    /// <summary>
    /// 诊断文本。
    /// </summary>
    private string _diagnosticText = "Waiting for source.";

    /// <summary>
    /// 错误文本。
    /// </summary>
    private string _errorText = string.Empty;

    /// <summary>
    /// 界面指定的录制输出路径。
    /// </summary>
    private string _recordingPath = string.Empty;

    /// <summary>
    /// 录制文本。
    /// </summary>
    private string _recordingText = "Not recording";

    /// <summary>
    /// 是否允许移除。
    /// </summary>
    private bool _canRemove = true;

    /// <summary>
    /// 是否已释放资源。
    /// </summary>
    private bool _disposed;

    /// <summary>
    /// 初始化播放器条目视图模型所需的状态和资源。
    /// </summary>
    public PlayerItemViewModel(int id, Func<Task<string?>> browse, Func<PlayerItemViewModel, Task> remove)
    {
        Title = $"Player {id}";
        _browse = browse;
        _remove = remove;
        Player = FluxEngine.CreatePlayer();
        Player.StateChanged += OnStateChanged;
        Player.Faulted += OnFaulted;

        BrowseCommand = new AsyncRelayCommand(BrowseAsync);
        OpenCommand = new AsyncRelayCommand(OpenAsync);
        PlayCommand = new AsyncRelayCommand(() => RunAsync(() => Player.PlayAsync().AsTask()));
        PauseCommand = new AsyncRelayCommand(() => RunAsync(() => Player.PauseAsync().AsTask()));
        StopCommand = new AsyncRelayCommand(StopAsync);
        RewindCommand = new AsyncRelayCommand(() => RunAsync(() => Player.SeekByAsync(TimeSpan.FromSeconds(-10)).AsTask()));
        ForwardCommand = new AsyncRelayCommand(() => RunAsync(() => Player.SeekByAsync(TimeSpan.FromSeconds(10)).AsTask()));
        StartRecordingCommand = new AsyncRelayCommand(StartRecordingAsync);
        StopRecordingCommand = new AsyncRelayCommand(() => RunAsync(() => Player.StopRecordingAsync().AsTask()));
        RemoveCommand = new AsyncRelayCommand(() => _remove(this));

        _diagnosticTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _diagnosticTimer.Tick += (_, _) => UpdateDiagnostics();
        _diagnosticTimer.Start();
    }

    /// <summary>
    /// 标题。
    /// </summary>
    public string Title { get; }

    /// <summary>
    /// 关联的播放器实例。
    /// </summary>
    public Player Player { get; }

    /// <summary>
    /// 核心提供的常用倍速档位。
    /// </summary>
    public IReadOnlyList<double> PlaybackSpeeds => FluxMedia.Playback.Player.SupportedSpeeds;

    /// <summary>
    /// 浏览命令。
    /// </summary>
    public ICommand BrowseCommand { get; }

    /// <summary>
    /// 打开命令。
    /// </summary>
    public ICommand OpenCommand { get; }

    /// <summary>
    /// 播放命令。
    /// </summary>
    public ICommand PlayCommand { get; }

    /// <summary>
    /// 暂停命令。
    /// </summary>
    public ICommand PauseCommand { get; }

    /// <summary>
    /// 停止命令。
    /// </summary>
    public ICommand StopCommand { get; }

    /// <summary>
    /// 后退命令。
    /// </summary>
    public ICommand RewindCommand { get; }

    /// <summary>
    /// 向前命令。
    /// </summary>
    public ICommand ForwardCommand { get; }

    /// <summary>
    /// 开始录制命令。
    /// </summary>
    public ICommand StartRecordingCommand { get; }

    /// <summary>
    /// 停止录制命令。
    /// </summary>
    public ICommand StopRecordingCommand { get; }

    /// <summary>
    /// 移除命令。
    /// </summary>
    public ICommand RemoveCommand { get; }

    /// <summary>
    /// 界面输入的媒体路径或网络地址。
    /// </summary>
    public string SourceText
    {
        get => _sourceText;
        set => SetField(ref _sourceText, value);
    }

    /// <summary>
    /// 状态文本。
    /// </summary>
    public string StateText
    {
        get => _stateText;
        private set => SetField(ref _stateText, value);
    }

    /// <summary>
    /// 录制路径。
    /// </summary>
    public string RecordingPath
    {
        get => _recordingPath;
        set => SetField(ref _recordingPath, value);
    }

    /// <summary>
    /// 录制文本。
    /// </summary>
    public string RecordingText
    {
        get => _recordingText;
        private set => SetField(ref _recordingText, value);
    }

    /// <summary>
    /// 诊断文本。
    /// </summary>
    public string DiagnosticText
    {
        get => _diagnosticText;
        private set => SetField(ref _diagnosticText, value);
    }

    /// <summary>
    /// 错误文本。
    /// </summary>
    public string ErrorText
    {
        get => _errorText;
        private set => SetField(ref _errorText, value);
    }

    /// <summary>
    /// 是否允许移除。
    /// </summary>
    public bool CanRemove
    {
        get => _canRemove;
        set => SetField(ref _canRemove, value);
    }

    /// <summary>
    /// 释放当前对象持有的资源。
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _diagnosticTimer.Stop();
        Player.StateChanged -= OnStateChanged;
        Player.Faulted -= OnFaulted;
        await Player.DisposeAsync();
    }

    /// <summary>
    /// 选择需要打开的本地媒体文件。
    /// </summary>
    private async Task BrowseAsync()
    {
        var path = await _browse();
        if (!string.IsNullOrWhiteSpace(path))
        {
            SourceText = path;
        }
    }

    /// <summary>
    /// 打开媒体源并读取媒体信息。
    /// </summary>
    private async Task OpenAsync()
    {
        var source = SourceText.Trim();
        if (string.IsNullOrWhiteSpace(source))
        {
            ErrorText = "Enter a file or URL.";
            return;
        }

        ErrorText = string.Empty;
        try
        {
            var result = await Player.OpenAndPlayAsync(source);
            if (!result.IsSuccess)
            {
                ErrorText = result.Message ?? "Open failed.";
            }
        }
        catch (Exception ex)
        {
            ErrorText = ex.Message;
        }

        UpdateDiagnostics();
    }

    /// <summary>
    /// 取消待执行操作并停止播放管线。
    /// </summary>
    private async Task StopAsync()
    {
        try
        {
            await Player.StopAsync();
            UpdateDiagnostics();
        }
        catch (Exception ex)
        {
            ErrorText = ex.Message;
        }
    }

    /// <summary>
    /// 开始录制当前播放的数据包。
    /// </summary>
    private async Task StartRecordingAsync()
    {
        var outputPath = RecordingPath.Trim();
        if (string.IsNullOrWhiteSpace(outputPath))
        {
            outputPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), $"FluxMedia-{Title.Replace(" ", "-")}-{DateTime.Now:yyyyMMdd-HHmmss}.mp4");
            RecordingPath = outputPath;
        }

        await RunAsync(() => Player.StartRecordingAsync(outputPath).AsTask());
        UpdateDiagnostics();
    }

    /// <summary>
    /// 执行界面操作，统一处理失败结果并刷新状态。
    /// </summary>
    private async Task RunAsync(Func<Task<PlaybackResult>> operation)
    {
        if (_disposed) return;
        try
        {
            var result = await operation();
            if (!result.IsSuccess)
            {
                ErrorText = result.Message ?? result.Error.ToString();
            }
        }
        catch (Exception ex)
        {
            ErrorText = ex.Message;
        }
    }

    /// <summary>
    /// 播放状态变化时刷新界面状态。
    /// </summary>
    private void OnStateChanged(object? sender, PlaybackState state) => Dispatcher.UIThread.Post(() => StateText = state.ToString());

    /// <summary>
    /// 收到播放错误时更新错误文本。
    /// </summary>
    private void OnFaulted(object? sender, FluxMedia.Core.Errors.FluxMediaError error) => Dispatcher.UIThread.Post(() => ErrorText = error.Message);

    /// <summary>
    /// 读取并显示最新播放诊断信息。
    /// </summary>
    private void UpdateDiagnostics()
    {
        if (_disposed)
        {
            return;
        }

        var d = Player.GetDiagnosticInfo();
        var recording = Player.GetRecordingDiagnosticInfo();
        RecordingText = $"{recording.State} · packets {recording.EncodedFrameCount}/{recording.ReceivedFrameCount}" + (string.IsNullOrWhiteSpace(recording.LastError) ? string.Empty : $" · {recording.LastError}");
        DiagnosticText =
            $"Decoder: {d.VideoDecoderBackend}\n" +
            $"Present: {d.VideoPresentationBackend}\n" +
            $"Hardware: requested={d.HardwareAccelerationRequested}, active={d.HardwareAccelerationActive}\n" +
            $"Fallback: {d.HardwareAccelerationFailureReason ?? "-"}\n" +
            $"Frames: ready={d.VideoFrameReadyCount}, decoded={d.DecodedFrameCount}, dropped={d.DroppedFrameCount}\n" +
            $"Worker: running={d.WorkerRunning}, exit={d.WorkerExitReason ?? "-"}";
    }
}

/// <summary>
/// 提供属性变更通知。
/// </summary>
public abstract class NotifyObject : INotifyPropertyChanged
{
    /// <summary>
    /// 绑定属性值变化时发出的通知。
    /// </summary>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// 字段值变化时赋值并发送属性通知。
    /// </summary>
    protected bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    /// <summary>
    /// 触发指定属性的变更通知。
    /// </summary>
    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

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
