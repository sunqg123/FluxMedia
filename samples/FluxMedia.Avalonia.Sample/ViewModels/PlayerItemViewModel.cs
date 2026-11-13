using Avalonia.Threading;
using FluxMedia.Avalonia.Sample.Commands;
using FluxMedia.Avalonia.Sample.Infrastructure;
using FluxMedia.Avalonia.Sample.Models;
using FluxMedia.Playback;
using System.Windows.Input;

namespace FluxMedia.Avalonia.Sample.ViewModels;

/// <summary>
/// 管理示例面板的媒体输入、录制和诊断。
/// </summary>
public sealed class PlayerItemViewModel : NotifyObject, IDisposable
{
    /// <summary>
    /// 媒体文件选择回调。
    /// </summary>
    private readonly Func<Task<string?>> _browse;

    /// <summary>
    /// 诊断信息刷新计时器。
    /// </summary>
    private readonly DispatcherTimer _diagnosticTimer;

    /// <summary>
    /// 播放诊断文本。
    /// </summary>
    private string _diagnosticText = "等待打开媒体。";

    /// <summary>
    /// 录制状态文本。
    /// </summary>
    private string _recordingText = "尚未录制";

    /// <summary>
    /// 输入或录制操作的错误。
    /// </summary>
    private string _errorText = string.Empty;

    /// <summary>
    /// 面板是否已释放。
    /// </summary>
    private bool _disposed;

    /// <summary>
    /// 关联外部播放器并初始化示例面板。
    /// </summary>
    public PlayerItemViewModel(Player player, Func<Task<string?>> browse)
    {
        Player = player;
        _browse = browse;
        BrowseCommand = new AsyncRelayCommand(BrowseAsync);
        OpenCommand = new AsyncRelayCommand(OpenAsync);
        StartRecordingCommand = new AsyncRelayCommand(StartRecordingAsync);
        StopRecordingCommand = new AsyncRelayCommand(() => RunAsync(() => Player.StopRecordingAsync().AsTask()));
        _diagnosticTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _diagnosticTimer.Tick += OnDiagnosticTick;
        _diagnosticTimer.Start();
        UpdateDiagnostics();
    }

    /// <summary>
    /// 页面提供的播放器，由页面负责释放。
    /// </summary>
    public Player Player
    {
        get;
    }

    /// <summary>
    /// 媒体地址和录制路径。
    /// </summary>
    public PlayerInput Input { get; } = new();

    /// <summary>
    /// 选择本地媒体。
    /// </summary>
    public ICommand BrowseCommand
    {
        get;
    }

    /// <summary>
    /// 打开输入地址并播放。
    /// </summary>
    public ICommand OpenCommand
    {
        get;
    }

    /// <summary>
    /// 开始录制。
    /// </summary>
    public ICommand StartRecordingCommand
    {
        get;
    }

    /// <summary>
    /// 停止录制。
    /// </summary>
    public ICommand StopRecordingCommand
    {
        get;
    }

    /// <summary>
    /// 播放诊断文本。
    /// </summary>
    public string DiagnosticText
    {
        get => _diagnosticText;
        private set => SetField(ref _diagnosticText, value);
    }

    /// <summary>
    /// 录制状态文本。
    /// </summary>
    public string RecordingText
    {
        get => _recordingText;
        private set => SetField(ref _recordingText, value);
    }

    /// <summary>
    /// 示例面板的操作错误。
    /// </summary>
    public string ErrorText
    {
        get => _errorText;
        private set => SetField(ref _errorText, value);
    }

    /// <summary>
    /// 停止诊断刷新，保留外部播放器。
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _diagnosticTimer.Stop();
        _diagnosticTimer.Tick -= OnDiagnosticTick;
    }

    /// <summary>
    /// 选择媒体文件并填入地址。
    /// </summary>
    private async Task BrowseAsync()
    {
        await RunAsync(async () =>
        {
            var path = await _browse();
            if (!_disposed && !string.IsNullOrWhiteSpace(path))
            {
                Input.Source = path;
            }
            return PlaybackResult.Success();
        });
    }

    /// <summary>
    /// 打开输入的媒体地址并播放。
    /// </summary>
    private Task OpenAsync()
    {
        var source = Input.Source.Trim();
        if (string.IsNullOrWhiteSpace(source))
        {
            ErrorText = "请输入文件路径或网络地址。";
            return Task.CompletedTask;
        }
        return RunAsync(() => Player.OpenAndPlayAsync(source).AsTask());
    }

    /// <summary>
    /// 开始录制，缺省路径时保存到桌面。
    /// </summary>
    private Task StartRecordingAsync()
    {
        var path = Input.RecordingPath.Trim();
        if (string.IsNullOrWhiteSpace(path))
        {
            path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                $"FluxMedia-{Player.InstanceId:N}-{DateTime.Now:yyyyMMdd-HHmmss}.mp4");
            Input.RecordingPath = path;
        }
        return RunAsync(() => Player.StartRecordingAsync(path).AsTask());
    }

    /// <summary>
    /// 执行面板操作并显示失败原因。
    /// </summary>
    private async Task RunAsync(Func<Task<PlaybackResult>> operation)
    {
        if (_disposed || Player.IsDisposed)
        {
            return;
        }
        ErrorText = string.Empty;
        try
        {
            var result = await operation();
            if (!_disposed && !result.IsSuccess)
            {
                ErrorText = result.Message ?? result.Error.ToString();
            }
        }
        catch (Exception exception)
        {
            if (!_disposed)
            {
                ErrorText = exception.Message;
            }
        }
        if (!_disposed)
        {
            UpdateDiagnostics();
        }
    }

    /// <summary>
    /// 响应诊断刷新计时。
    /// </summary>
    private void OnDiagnosticTick(object? sender, EventArgs e) => UpdateDiagnostics();

    /// <summary>
    /// 刷新播放和录制诊断文本。
    /// </summary>
    private void UpdateDiagnostics()
    {
        if (_disposed || Player.IsDisposed)
        {
            return;
        }
        var diagnostic = Player.GetDiagnosticInfo();
        var recording = Player.GetRecordingDiagnosticInfo();
        RecordingText = recording is null ? "录制诊断暂不可用" : $"{recording.State} · 已写包 {recording.EncodedFrameCount}"
            + (recording.OutputFileSize.HasValue ? $" · {recording.OutputFileSize.Value} 字节" : string.Empty)
            + (string.IsNullOrWhiteSpace(recording.LastError) ? string.Empty : $" · {recording.LastError}");
        if (diagnostic is null)
        {
            DiagnosticText = "媒体状态正在切换，诊断快照暂不可用。";
            return;
        }
        DiagnosticText = $"状态：{diagnostic.State}，时序：{diagnostic.TimingMode?.ToString() ?? "未建立管线"}\n"
            + $"协议：{diagnostic.Protocol ?? "未打开"}，容器：{diagnostic.Container ?? "未知"}\n"
            + $"视频帧率：{diagnostic.FrameRate?.ToString("0.###") ?? "未知"}\n"
            + $"视频缓冲：{diagnostic.VideoBufferedDuration?.TotalMilliseconds.ToString("0") ?? "未知"} ms\n"
            + $"音频设备：{diagnostic.AudioOutputDeviceState ?? "未创建"}\n"
            + $"当前倍速：{diagnostic.AppliedRate:0.###}，{(diagnostic.IsSpeedChangePending ? "等待应用" : "无待应用请求")}\n"
            + $"允许倍速：{diagnostic.MinRate}～{diagnostic.MaxRate}\n"
            + $"最近换速请求：编号={diagnostic.RateRequestId}，目标={diagnostic.RequestedRate:0.###}，结果={diagnostic.RateChangeState}\n"
            + $"最近换速错误：{diagnostic.LastRateError ?? "-"}";
    }
}
