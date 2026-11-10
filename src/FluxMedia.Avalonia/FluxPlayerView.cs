using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Rendering.Composition;
using Avalonia.Threading;
using FluxMedia.Core.Configuration;
using FluxMedia.Core.Logging;
using FluxMedia.Core.Models;
using FluxMedia.Playback;
using System.Diagnostics;
using AvaloniaPixelFormat = Avalonia.Platform.PixelFormat;
using FluxPixelFormat = FluxMedia.Core.Models.PixelFormat;
using NumericsVector2 = System.Numerics.Vector2;
using NumericsVector3 = System.Numerics.Vector3;

namespace FluxMedia.Avalonia;

/// <summary>
/// 在 Avalonia 控件中显示播放器视频帧。
/// </summary>
public sealed class FluxPlayerView : UserControl
{
    /// <summary>
    /// 播放器属性。
    /// </summary>
    public static readonly StyledProperty<Player?> PlayerProperty = AvaloniaProperty.Register<FluxPlayerView, Player?>(nameof(Player));

    /// <summary>
    /// 缩放属性。
    /// </summary>
    public static readonly StyledProperty<Stretch> StretchProperty = AvaloniaProperty.Register<FluxPlayerView, Stretch>(nameof(Stretch), Stretch.Uniform);

    /// <summary>
    /// 承载 CPU 位图输出的图像控件。
    /// </summary>
    private readonly Image _image;

    /// <summary>
    /// 保护待显示帧和渲染状态的锁。
    /// </summary>
    private readonly object _frameSync = new();

    /// <summary>
    /// CPU 显示路径复用的可写位图。
    /// </summary>
    private WriteableBitmap? _bitmap;

    /// <summary>
    /// 当前已订阅视频帧事件的播放器。
    /// </summary>
    private Player? _attachedPlayer;

    /// <summary>
    /// 等待下一次界面渲染的视频帧租约。
    /// </summary>
    private VideoFrameLease? _latestFrame;

    /// <summary>
    /// 是否已安排一次界面渲染。
    /// </summary>
    private int _renderQueued;

    /// <summary>
    /// 控件是否已从可视树移除。
    /// </summary>
    private volatile bool _isDetached = true;

    /// <summary>
    /// 是否已订阅播放器视频帧事件。
    /// </summary>
    private bool _isSubscribed;

    /// <summary>
    /// 承载共享 GPU 图像的合成器可视对象。
    /// </summary>
    private CompositionSurfaceVisual? _gpuVisual;

    /// <summary>
    /// 负责导入和显示共享纹理的呈现器。
    /// </summary>
    private GpuVideoPresenter? _gpuPresenter;

    /// <summary>
    /// 最近一次 GPU 图像导入或显示失败的原因。
    /// </summary>
    private string? _gpuPresentationError;

    /// <summary>
    /// 是否已成功显示 GPU 帧。
    /// </summary>
    private bool _gpuFramePresented;

    /// <summary>
    /// 控件接收到的视频帧总数。
    /// </summary>
    private long _receivedFrameCount;

    /// <summary>
    /// 实际提交显示的视频帧总数。
    /// </summary>
    private long _renderedFrameCount;

    /// <summary>
    /// 每代仅记录一次实际显示提交。
    /// </summary>
    private long _lastPresentedTimeline;

    /// <summary>
    /// 渲染前被更新帧替换的帧数。
    /// </summary>
    private long _replacedFrameCount;

    /// <summary>
    /// 最近一次位图复制耗时（时钟刻度）。
    /// </summary>
    private long _lastUploadElapsedTicks;

    /// <summary>
    /// 初始化 Flux 播放器视图所需的状态和资源。
    /// </summary>
    public FluxPlayerView()
    {
        _image = new Image { Stretch = global::Avalonia.Media.Stretch.Uniform };
        Content = _image;
        Background = Brushes.Black;
    }

    /// <summary>
    /// 当前显示的播放器。
    /// </summary>
    public Player? Player
    {
        get => GetValue(PlayerProperty);
        set => SetValue(PlayerProperty, value);
    }

    /// <summary>
    /// 位图显示时的缩放方式。
    /// </summary>
    public Stretch Stretch
    {
        get => GetValue(StretchProperty);
        set => SetValue(StretchProperty, value);
    }

    /// <summary>
    /// 获取当前运行状态和诊断信息。
    /// </summary>
    public FluxPlayerViewDiagnosticInfo GetDiagnosticInfo()
    {
        return new FluxPlayerViewDiagnosticInfo
        {
            PresentationPipeline = _gpuFramePresented ? "GPU shared texture -> Avalonia compositor" : "CPU BGRA -> WriteableBitmap -> Avalonia compositor",
            BackendSelection = "Automatic (configured by UsePlatformDetect in the host application)",
            UsesAvaloniaCompositor = true,
            IsZeroCopyGpuPresentation = _gpuFramePresented,
            GpuPresentationError = _gpuPresentationError,
            ReceivedFrameCount = Interlocked.Read(ref _receivedFrameCount),
            RenderedFrameCount = Interlocked.Read(ref _renderedFrameCount),
            ReplacedFrameCount = Interlocked.Read(ref _replacedFrameCount),
            LastBitmapUploadElapsed = TimeSpan.FromTicks(Interlocked.Read(ref _lastUploadElapsedTicks)),
            PixelSize = _bitmap?.PixelSize
        };
    }

    /// <summary>
    /// 清除当前位图和等待显示的帧。
    /// </summary>
    public void Clear()
    {
        lock (_frameSync)
        {
            _bitmap?.Dispose();
            _bitmap = null;
            _latestFrame?.Dispose();
            _latestFrame = null;
            Interlocked.Exchange(ref _renderQueued, 0);
        }

        _image.Source = null;
        _image.InvalidateVisual();
    }

    /// <summary>
    /// 控件显示时关联播放器和显示资源。
    /// </summary>
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _isDetached = false;
        InitializeGpuPresenter();
        AttachPlayer(Player);
        SubscribeToPlayer();
    }

    /// <summary>
    /// 控件移除时解除订阅并清理显示资源。
    /// </summary>
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _isDetached = true;
        UnsubscribeFromPlayer();
        DisposeGpuPresenter();
        Clear();
        base.OnDetachedFromVisualTree(e);
    }

    /// <summary>
    /// 响应控件属性变更并更新显示配置。
    /// </summary>
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == PlayerProperty)
        {
            AttachPlayer(change.GetNewValue<Player?>());
            ConfigurePlayerVideoOutput();
        }
        else if (change.Property == StretchProperty)
        {
            _image.Stretch = change.GetNewValue<Stretch>();
        }
    }

    /// <summary>
    /// 布局控件并同步 GPU 显示区域。
    /// </summary>
    protected override Size ArrangeOverride(Size finalSize)
    {
        var arranged = base.ArrangeOverride(finalSize);
        if (_gpuVisual is not null)
        {
            UpdateGpuVisualLayout(_gpuVisual, arranged);
        }

        return arranged;
    }

    /// <summary>
    /// 切换当前播放器并重新订阅视频帧。
    /// </summary>
    private void AttachPlayer(Player? player)
    {
        if (ReferenceEquals(_attachedPlayer, player))
        {
            SubscribeToPlayer();
            return;
        }

        UnsubscribeFromPlayer();
        _attachedPlayer = player;
        Clear();
        SubscribeToPlayer();
    }

    /// <summary>
    /// 订阅播放器的视频帧通知。
    /// </summary>
    private void SubscribeToPlayer()
    {
        if (_isDetached || _isSubscribed || _attachedPlayer is null) return;
        _attachedPlayer.VideoFrameReady += OnVideoFrameReady;
        _isSubscribed = true;
    }

    /// <summary>
    /// 解除播放器的视频帧订阅。
    /// </summary>
    private void UnsubscribeFromPlayer()
    {
        if (!_isSubscribed || _attachedPlayer is null) return;
        _attachedPlayer.VideoFrameReady -= OnVideoFrameReady;
        _isSubscribed = false;
    }

    /// <summary>
    /// 保留最新帧，并合并界面渲染请求。
    /// </summary>
    private void OnVideoFrameReady(object? sender, PlaybackVideoFrameEventArgs args)
    {
        if (_isDetached || !args.Frame.IsCurrent) return;
        var lease = args.Frame.Retain();
        Interlocked.Increment(ref _receivedFrameCount);
        lock (_frameSync)
        {
            if (_latestFrame is not null)
            {
                _latestFrame.Dispose();
                Interlocked.Increment(ref _replacedFrameCount);
            }
            _latestFrame = lease;
            if (Interlocked.Exchange(ref _renderQueued, 1) == 1) return;
        }

        Dispatcher.UIThread.Post(RenderLatestFrame, DispatcherPriority.Render);
    }

    /// <summary>
    /// 取出最新帧，选择 GPU 或位图显示。
    /// </summary>
    private void RenderLatestFrame()
    {
        if (_isDetached)
        {
            ReleasePendingFrame();
            return;
        }

        VideoFrameLease? lease;
        lock (_frameSync)
        {
            lease = _latestFrame;
            _latestFrame = null;
        }

        if (lease is null) { Interlocked.Exchange(ref _renderQueued, 0); return; }
        try
        {
            if (!lease.Frame.IsCurrent) { Interlocked.Exchange(ref _renderQueued, 0); return; }
            if (lease.Frame.GpuSurface is not null && _gpuPresenter is not null)
            {
                _ = PresentGpuFrameAsync(lease);
                lease = null;
                return;
            }

            Interlocked.Exchange(ref _renderQueued, 0);
            RenderFrame(lease.Frame);
        }
        finally
        {
            lease?.Dispose();
            if (lease is not null)
            {
                QueuePendingFrameIfNeeded();
            }
        }
    }

    /// <summary>
    /// 将 BGRA 像素复制到显示位图。
    /// </summary>
    private void RenderFrame(VideoFrame frame)
    {
        if (!frame.IsCurrent || frame.Format.PixelFormat != FluxPixelFormat.Bgra32 || frame.Format.Width <= 0 || frame.Format.Height <= 0 || frame.Buffer.IsEmpty) return;
        var startedAt = Stopwatch.GetTimestamp();
        EnsureBitmap(frame.Format.Width, frame.Format.Height);
        if (_bitmap is null) return;
        using (var framebuffer = _bitmap.Lock())
        {
            CopyFrame(frame, framebuffer);
        }
        if (!frame.IsCurrent) return;
        if (!ReferenceEquals(_image.Source, _bitmap))
        {
            _image.Source = _bitmap;
        }
        Interlocked.Exchange(ref _lastUploadElapsedTicks, ElapsedSince(startedAt).Ticks);
        Interlocked.Increment(ref _renderedFrameCount);
        _image.IsVisible = true;
        _image.InvalidateVisual();
        LogTimelinePresentation(frame, "CPU");
    }

    /// <summary>
    /// 将首个显示提交与核心时间线对应起来。
    /// </summary>
    private void LogTimelinePresentation(VideoFrame frame, string backend)
    {
        if (!frame.IsCurrent || frame.TimelineId == 0 || frame.TimelineId == _lastPresentedTimeline) return;
        _lastPresentedTimeline = frame.TimelineId;
        FluxMediaLog.Write(LogLevel.Information, nameof(FluxPlayerView), Player?.InstanceId ?? Guid.Empty,
            $"Event=VideoTimelinePresented Timeline={frame.TimelineId} Timestamp={frame.Timestamp} Backend={backend}");
    }

    /// <summary>
    /// 释放尚未提交显示的帧租约。
    /// </summary>
    private void ReleasePendingFrame()
    {
        lock (_frameSync)
        {
            _latestFrame?.Dispose();
            _latestFrame = null;
            Interlocked.Exchange(ref _renderQueued, 0);
        }
    }

    /// <summary>
    /// 按视频尺寸创建或复用显示位图。
    /// </summary>
    private void EnsureBitmap(int width, int height)
    {
        var pixelSize = new PixelSize(width, height);
        if (_bitmap is not null && _bitmap.PixelSize == pixelSize) return;
        _bitmap?.Dispose();
        _bitmap = new WriteableBitmap(pixelSize, new Vector(96, 96), AvaloniaPixelFormat.Bgra8888, AlphaFormat.Unpremul);
    }

    /// <summary>
    /// 异步创建合成器的 GPU 显示资源。
    /// </summary>
    private async void InitializeGpuPresenter()
    {
        try
        {
            var elementVisual = ElementComposition.GetElementVisual(this);
            if (elementVisual is null) return;
            var compositor = elementVisual.Compositor;
            var interop = await compositor.TryGetCompositionGpuInterop();
            if (_isDetached || interop is null) return;
            var surface = compositor.CreateDrawingSurface();
            _gpuPresenter = new GpuVideoPresenter(surface, interop);
            _gpuVisual = compositor.CreateSurfaceVisual();
            _gpuVisual.Surface = surface;
            UpdateGpuVisualLayout(_gpuVisual, Bounds.Size);
            ElementComposition.SetElementChildVisual(this, _gpuVisual);
            ConfigurePlayerVideoOutput();
        }
        catch (Exception ex)
        {
            _gpuPresentationError = ex.Message;
            ConfigurePlayerVideoOutput(disableGpu: true);
        }
    }

    /// <summary>
    /// 提交 GPU 帧，失败时切换为 CPU 输出。
    /// </summary>
    private async Task PresentGpuFrameAsync(VideoFrameLease lease)
    {
        try
        {
            var surface = lease.Frame.GpuSurface;
            var presenter = _gpuPresenter;
            if (surface is null || presenter is null || !lease.Frame.IsCurrent) return;
            await presenter.PresentAsync(surface);
            if (!lease.Frame.IsCurrent) return;
            _gpuFramePresented = true;
            _gpuPresentationError = null;
            _image.IsVisible = false;
            Interlocked.Increment(ref _renderedFrameCount);
            LogTimelinePresentation(lease.Frame, "GPU");
        }
        catch (Exception ex)
        {
            _gpuFramePresented = false;
            _gpuPresentationError = ex.Message;
            ConfigurePlayerVideoOutput(disableGpu: true);
        }
        finally
        {
            lease.Dispose();
            Interlocked.Exchange(ref _renderQueued, 0);
            QueuePendingFrameIfNeeded();
        }
    }

    /// <summary>
    /// 仍有待显示帧时再次安排渲染。
    /// </summary>
    private void QueuePendingFrameIfNeeded()
    {
        lock (_frameSync)
        {
            if (_isDetached || _latestFrame is null || Interlocked.Exchange(ref _renderQueued, 1) == 1) return;
        }

        Dispatcher.UIThread.Post(RenderLatestFrame, DispatcherPriority.Render);
    }

    /// <summary>
    /// 将显示端支持的 GPU 能力传给播放器。
    /// </summary>
    private void ConfigurePlayerVideoOutput(bool disableGpu = false)
    {
        var player = Player;
        if (player is null) return;
        var presenter = disableGpu ? null : _gpuPresenter;
        player.ConfigureVideoOutput(new VideoOutputCapabilities
        {
            SupportsD3D11SharedTexture = presenter?.SupportsD3D11SharedTexture == true,
            SupportsVulkanOpaqueFileDescriptor = presenter?.SupportsVulkanOpaqueFileDescriptor == true,
            AdapterLuid = presenter?.AdapterLuid,
            DeviceUuid = presenter?.DeviceUuid
        });
    }

    /// <summary>
    /// 断开 GPU 显示并异步释放导入资源。
    /// </summary>
    private void DisposeGpuPresenter()
    {
        ConfigurePlayerVideoOutput(disableGpu: true);
        ElementComposition.SetElementChildVisual(this, null);
        _gpuVisual = null;
        var presenter = _gpuPresenter;
        _gpuPresenter = null;
        if (presenter is not null)
        {
            _ = presenter.DisposeAsync();
        }
    }

    /// <summary>
    /// 更新 GPU 显示区域的尺寸和方向。
    /// </summary>
    private static void UpdateGpuVisualLayout(CompositionSurfaceVisual visual, Size size)
    {
        var width = (float)Math.Max(0, size.Width);
        var height = (float)Math.Max(0, size.Height);
        visual.Size = new NumericsVector2(width, height);
        visual.CenterPoint = new NumericsVector3(width / 2, height / 2, 0);
        visual.Scale = new NumericsVector3(1, -1, 1);
    }

    /// <summary>
    /// 计算从指定计时点开始经过的时长。
    /// </summary>
    private static TimeSpan ElapsedSince(long startedAt)
    {
        var elapsedTimestamp = Stopwatch.GetTimestamp() - startedAt;
        return TimeSpan.FromSeconds((double)elapsedTimestamp / Stopwatch.Frequency);
    }

    /// <summary>
    /// 按源和目标行跨度复制像素。
    /// </summary>
    private static unsafe void CopyFrame(VideoFrame frame, ILockedFramebuffer framebuffer)
    {
        var widthBytes = checked(frame.Format.Width * 4);
        var sourceStride = frame.Stride > 0 ? frame.Stride : widthBytes;
        var source = frame.Buffer.Span;
        var rows = Math.Min(frame.Format.Height, framebuffer.Size.Height);
        var copyBytesPerRow = Math.Min(widthBytes, framebuffer.RowBytes);

        fixed (byte* sourcePointer = source)
        {
            var destinationPointer = (byte*)framebuffer.Address;
            var contiguousLength = checked(rows * widthBytes);
            if (sourceStride == widthBytes && framebuffer.RowBytes == widthBytes && contiguousLength <= source.Length)
            {
                Buffer.MemoryCopy(sourcePointer, destinationPointer, contiguousLength, contiguousLength);
                return;
            }

            for (var row = 0; row < rows; row++)
            {
                var sourceOffset = row * sourceStride;
                if (sourceOffset >= source.Length) break;
                var available = Math.Min(copyBytesPerRow, source.Length - sourceOffset);
                if (available <= 0) break;
                var destination = destinationPointer + row * framebuffer.RowBytes;
                Buffer.MemoryCopy(sourcePointer + sourceOffset, destination, available, available);
            }
        }
    }
}
