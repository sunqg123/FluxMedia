namespace FluxMedia.Core.Models;

/// <summary>
/// 托管视频帧。
/// </summary>
public sealed class VideoFrame : IDisposable
{
    /// <summary>
    /// 持有底层内存或纹理的资源对象。
    /// </summary>
    private readonly IVideoFrameOwner? _owner;

    /// <summary>
    /// 当前持有的像素或音频缓冲区。
    /// </summary>
    private readonly ReadOnlyMemory<byte> _buffer;

    /// <summary>
    /// 该帧所属的跳转代次。
    /// </summary>
    private VideoFrameEpoch? _seekEpoch;

    /// <summary>
    /// 生成该帧的管线代次。
    /// </summary>
    private VideoFrameEpoch? _producerEpoch;

    /// <summary>
    /// 保护帧租约和原始引用的锁。
    /// </summary>
    private readonly object _lifetimeSync = new();

    /// <summary>
    /// 是否已释放资源。
    /// </summary>
    private bool _disposed;

    /// <summary>
    /// 尚未释放的视频帧租约数。
    /// </summary>
    private int _activeLeaseCount;

    /// <summary>
    /// 初始化视频帧所需的状态和资源。
    /// </summary>
    public VideoFrame(VideoFormat format, ReadOnlyMemory<byte> buffer, TimeSpan timestamp, long sequence, int stride = 0)
    {
        Format = format;
        _buffer = buffer;
        Timestamp = timestamp;
        Sequence = sequence;
        Stride = stride;
    }

    /// <summary>
    /// 初始化视频帧所需的状态和资源。
    /// </summary>
    internal VideoFrame(VideoFormat format, IVideoFrameMemoryOwner owner, int length, TimeSpan timestamp, long sequence, int stride = 0)
    {
        Format = format;
        _owner = owner;
        _buffer = owner.Memory.Slice(0, length);
        Timestamp = timestamp;
        Sequence = sequence;
        Stride = stride;
    }

    /// <summary>
    /// 初始化视频帧所需的状态和资源。
    /// </summary>
    internal VideoFrame(VideoFormat format, IVideoFrameOwner owner, GpuVideoSurface gpuSurface, TimeSpan timestamp, long sequence)
    {
        Format = format;
        _owner = owner;
        GpuSurface = gpuSurface;
        Timestamp = timestamp;
        Sequence = sequence;
    }

    /// <summary>
    /// 格式。
    /// </summary>
    public VideoFormat Format { get; }

    /// <summary>
    /// 帧像素数据；有效期受帧和租约控制。
    /// </summary>
    public ReadOnlyMemory<byte> Buffer => _buffer;

    /// <summary>
    /// 供显示端导入的 GPU 表面。
    /// </summary>
    public GpuVideoSurface? GpuSurface { get; }

    /// <summary>
    /// 是否使用 GPU 帧。
    /// </summary>
    public bool IsGpuBacked => GpuSurface is not null;

    /// <summary>
    /// 时间戳。
    /// </summary>
    public TimeSpan Timestamp { get; }

    /// <summary>
    /// 序号。
    /// </summary>
    public long Sequence { get; }

    /// <summary>
    /// 跳转或退役后旧帧失效，异步显示前应再次检查。
    /// </summary>
    public bool IsCurrent => (_seekEpoch?.IsCurrent ?? true) && (_producerEpoch?.IsCurrent ?? true);

    /// <summary>
    /// 跳转时间线编号，零表示外部创建的未标记帧。
    /// </summary>
    public long TimelineId => _seekEpoch?.Id ?? 0;

    /// <summary>
    /// 核对该帧是否来自指定跳转。
    /// </summary>
    internal bool IsFromEpoch(VideoFrameEpoch epoch) => ReferenceEquals(_seekEpoch, epoch);

    /// <summary>
    /// 发布之前固定帧所属代次。
    /// </summary>
    internal void BindEpoch(VideoFrameEpoch? seekEpoch, VideoFrameEpoch producerEpoch)
    {
        _seekEpoch = seekEpoch;
        _producerEpoch = producerEpoch;
    }

    /// <summary>
    /// 每行像素占用的字节数。
    /// </summary>
    public int Stride { get; }

    /// <summary>
    /// 增加帧引用，供异步处理或跨回调持有。
    /// </summary>
    public VideoFrameLease Retain()
    {
        lock (_lifetimeSync)
        {
            // 生产者释放后，已有租约仍可继续保留此帧。
            if (_disposed && _activeLeaseCount == 0)
            {
                throw new ObjectDisposedException(nameof(VideoFrame));
            }
            _owner?.AddReference();
            _activeLeaseCount++;
            return new VideoFrameLease(this);
        }
    }

    /// <summary>
    /// 释放当前对象持有的资源。
    /// </summary>
    public void Dispose()
    {
        lock (_lifetimeSync)
        {
            if (_disposed) return;
            _disposed = true;
            _owner?.Release();
        }
    }

    /// <summary>
    /// 释放一个租约持有的底层资源引用。
    /// </summary>
    internal void ReleaseLease()
    {
        lock (_lifetimeSync)
        {
            if (_activeLeaseCount <= 0) return;
            _activeLeaseCount--;
            _owner?.Release();
        }
    }
}
