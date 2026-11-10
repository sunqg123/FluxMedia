using Avalonia.Threading;
using FluxMedia.Avalonia.Sample.Commands;
using FluxMedia.Avalonia.Sample.Infrastructure;
using FluxMedia.Avalonia.Sample.Models;
using FluxMedia.Playback;
using System.Windows.Input;

namespace FluxMedia.Avalonia.Sample.ViewModels;

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
    /// 当前播放器的媒体与录制输入。
    /// </summary>
    public PlayerInput Input { get; } = new();

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
    /// 状态文本。
    /// </summary>
    public string StateText
    {
        get => _stateText;
        private set => SetField(ref _stateText, value);
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
            Input.Source = path;
        }
    }

    /// <summary>
    /// 打开媒体源并读取媒体信息。
    /// </summary>
    private async Task OpenAsync()
    {
        var source = Input.Source.Trim();
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
        var outputPath = Input.RecordingPath.Trim();
        if (string.IsNullOrWhiteSpace(outputPath))
        {
            outputPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), $"FluxMedia-{Title.Replace(" ", "-")}-{DateTime.Now:yyyyMMdd-HHmmss}.mp4");
            Input.RecordingPath = outputPath;
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
