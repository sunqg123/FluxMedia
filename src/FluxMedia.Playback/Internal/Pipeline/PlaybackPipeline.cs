using FluxMedia.Core.Configuration;
using FluxMedia.Core.Logging;
using FluxMedia.Core.Models;
using FluxMedia.FFmpeg.Pipeline;
using FluxMedia.Playback.Internal.Audio;

namespace FluxMedia.Playback.Internal;

/// <summary>
/// 独占一次播放管线的线程、取消信号和原生资源。
/// </summary>
internal sealed partial class PlaybackPipeline : IAsyncDisposable
{
    /// <summary>
    /// 管线固定的媒体源。
    /// </summary>
    private readonly MediaSource _source;
    /// <summary>
    /// 本次运行的播放配置。
    /// </summary>
    private readonly PlaybackOptions _options;
    /// <summary>
    /// 所属播放器标识。
    /// </summary>
    private readonly Guid _instanceId;
    /// <summary>
    /// 本次管线的开始位置。
    /// </summary>
    private readonly TimeSpan _startPosition;
    /// <summary>
    /// 当前管线的控制及通知回调。
    /// </summary>
    private readonly PlaybackPipelineCallbacks _callbacks;
    /// <summary>
    /// 从打开媒体到管线退役的取消信号。
    /// </summary>
    private readonly CancellationTokenSource _lifetime;
    /// <summary>
    /// 仅属于本次管线的帧泵。
    /// </summary>
    private readonly PlaybackFramePump _frames;
    /// <summary>
    /// 保护重复释放共享同一任务。
    /// </summary>
    private readonly object _stopSync = new();
    /// <summary>
    /// 视频解码器。
    /// </summary>
    private FFmpegPacketVideoPlaybackWorker? _video;
    /// <summary>
    /// 音频解码器。
    /// </summary>
    private FFmpegPacketAudioPlaybackWorker? _audio;
    /// <summary>
    /// 管线独占的音频设备。
    /// </summary>
    private IAudioOutputDevice? _audioOutput;
    /// <summary>
    /// 解复用、音频、视频及帧泵的实际线程任务。
    /// </summary>
    private readonly List<Task> _tasks = new();
    /// <summary>
    /// 独立保存解复用任务，避免依赖列表顺序。
    /// </summary>
    private Task? _demuxTask;
    /// <summary>
    /// 唯一的资源释放任务。
    /// </summary>
    private Task? _stopTask;
    /// <summary>
    /// 是否已发出停止信号。
    /// </summary>
    private int _stopping;

    /// <summary>
    /// 本会话的换速协调器。
    /// </summary>
    private readonly PlaybackRateController _rates;

    /// <summary>
    /// 控制操作触发的管线故障，取消不能覆盖它。
    /// </summary>
    private Exception? _controlFailure;

    /// <summary>
    /// 换速提交校验使用的管线代次。
    /// </summary>
    public long RateGeneration { get; }

    /// <summary>
    /// 创建独立管线，打开、重播、跳转重建和重连均使用此入口。
    /// </summary>
    public PlaybackPipeline(MediaSource source, PlaybackOptions options, Guid instanceId, TimeSpan position, PlaybackVideoFrameHub output, PlaybackPipelineCallbacks callbacks, CancellationToken operationToken, PlaybackRateController rates)
    {
        _source = source;
        _options = options;
        _instanceId = instanceId;
        _startPosition = position;
        _callbacks = callbacks;
        _rates = rates;
        RateGeneration = rates.BindPipeline(callbacks.Resync().Version);
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(operationToken);
        // 视频链路③绑定：帧泵分发后调用会话发布回调。
        _frames = new PlaybackFramePump(output, callbacks.PublishFrame);
        Demuxer = new FFmpegSharedDemuxer(source, options, instanceId, position, deferPacketReading: true);
    }
    /// <summary>
    /// 当前管线的包分发及录制入口。
    /// </summary>
    public FFmpegSharedDemuxer Demuxer { get; }
    /// <summary>
    /// 视频、尾帧及音频尾音均结束后的完成任务。
    /// </summary>
    public Task? Completion { get; private set; }
    /// <summary>
    /// 解复用任务是否已退出。
    /// </summary>
    public bool DemuxerExited => _demuxTask?.IsCompleted == true;
    /// <summary>
    /// 管线是否已被取消。
    /// </summary>
    public bool IsCanceled => Volatile.Read(ref _stopping) != 0 || _lifetime.IsCancellationRequested;

    /// <summary>
    /// 控制提交故障不能误当作网络断流自动重连。
    /// </summary>
    public bool HasControlFailure => Volatile.Read(ref _controlFailure) is not null;
    /// <summary>
    /// 当前音频线程提供的同步时钟。
    /// </summary>
    public AudioClockSnapshot? AudioClock => _audio?.Clock;

    /// <summary>
    /// 当前音频设备是否可用。
    /// </summary>
    public bool AudioOutputAvailable => _audioOutput?.IsAvailable == true;
    /// <summary>
    /// 当前音频输出后端。
    /// </summary>
    public string AudioBackend => _audioOutput?.BackendName ?? "None";

    /// <summary>
    /// 打开一次媒体并停留在读包闸门，避免再次打开本地文件。
    /// </summary>
    public async Task PrepareAsync(CancellationToken cancellationToken)
    {
        if (_demuxTask is not null) throw new InvalidOperationException("管线只能打开一次。");
        var demuxTask = Demuxer.StartAsync(_lifetime.Token);
        _demuxTask = demuxTask;
        _tasks.Add(demuxTask);
        _ = ObserveDemuxerAsync(demuxTask);
        await Demuxer.WaitUntilReadyAsync(cancellationToken).ConfigureAwait(false);
        _rates.SetAudioTrack(RateGeneration, Demuxer.AudioStream is not null);
    }
    /// <summary>
    /// 统一创建解码器及帧泵，然后放行读包。
    /// </summary>
    public void Start()
    {
        if (IsCanceled) throw new OperationCanceledException(_lifetime.Token);
        if (Completion is not null) throw new InvalidOperationException("管线已经启动。");
        if (Demuxer.VideoStream is null) throw new InvalidOperationException("当前媒体没有视频流。");
        var token = _lifetime.Token;
        var pumpTask = _frames.RunAsync(token);
        _tasks.Add(pumpTask);
        var workerContext = new PlaybackWorkerContext(
            source: _source,
            instanceId: _instanceId,
            startPosition: _startPosition,
            timingMode: Demuxer.TimingMode,
            isPaused: _callbacks.IsPaused,
            playbackRate: _callbacks.PlaybackRate,
            resync: _callbacks.Resync);
        Func<long> packetGeneration = () => Demuxer.PacketGeneration;
        FFmpegPacketConsumer packetConsumed = packet => Demuxer.WriteRecordingPacket(packet);

        if (Demuxer.AudioStream is not null)
        {
            _audioOutput = AudioOutputDeviceFactory.Create(_instanceId);
            var audioInput = new FFmpegPacketInput(Demuxer.AudioStream, Demuxer.AudioPackets, packetGeneration);
            var audioCallbacks = new AudioPlaybackWorkerCallbacks(
                volume: _callbacks.Volume,
                isMuted: _callbacks.IsMuted,
                packetConsumed: packetConsumed);
            _rates.AudioStarting(RateGeneration);
            _audio = new FFmpegPacketAudioPlaybackWorker(workerContext, audioInput, _audioOutput, audioCallbacks,
                _rates, RateGeneration, _rates.MaxAudioTempoStages);
        }

        var videoInput = new FFmpegPacketInput(Demuxer.VideoStream, Demuxer.VideoPackets, packetGeneration);
        var videoCallbacks = new VideoPlaybackWorkerCallbacks(
            // 视频链路①绑定：解码输出交给帧泵接收。
            frameReady: _frames.Queue,
            // 播放时钟绑定：直接读取本代音频线程的时钟。
            audioClock: () => AudioClock,
            bufferedDuration: () => Demuxer.VideoBufferedDuration,
            bufferedPacketCount: () => Demuxer.VideoPacketCount,
            videoCapabilities: _callbacks.VideoCapabilities,
            packetConsumed: packetConsumed);
        _video = new FFmpegPacketVideoPlaybackWorker(workerContext, videoInput, _options, videoCallbacks);
        var videoTask = _video.RunAsync(token);
        var audioTask = _audio?.RunAsync(token) ?? Task.CompletedTask;
        _tasks.Add(videoTask);
        _tasks.Add(audioTask);
        Demuxer.ActivatePacketReading();
        Completion = RunToCompletionAsync(videoTask, audioTask, pumpTask);
    }
    /// <summary>
    /// 解复用失败时中断暂停中的解码器，交由统一完成任务报告异常。
    /// </summary>
    private async Task ObserveDemuxerAsync(Task demuxTask)
    {
        try { await demuxTask.ConfigureAwait(false); }
        catch
        {
            // 保留解复用原始异常，避免先取消帧泵而掩盖断流原因。
            SignalStop(() => _video?.Dispose());
            SignalStop(() => _audio?.Dispose());
        }
    }
    /// <summary>
    /// 先排空解码器及尾帧，再完成整个管线。
    /// </summary>
    private async Task RunToCompletionAsync(Task videoTask, Task audioTask, Task pumpTask)
    {
        try
        {
            var demuxTask = _demuxTask ?? throw new InvalidOperationException("管线尚未打开。");
            var pending = new List<Task> { demuxTask, videoTask, audioTask, pumpTask };
            var framesCompleted = false;
            while (pending.Count > 0)
            {
                // 排空期间仍观察所有任务，任一失败都立即取消管线。
                var completed = await Task.WhenAny(pending).ConfigureAwait(false);
                await completed.ConfigureAwait(false);
                pending.Remove(completed);

                if (!framesCompleted && demuxTask.IsCompletedSuccessfully && videoTask.IsCompletedSuccessfully)
                {
                    _lifetime.Token.ThrowIfCancellationRequested();
                    _frames.Complete();
                    framesCompleted = true;
                }
            }
            _lifetime.Token.ThrowIfCancellationRequested();
        }
        catch
        {
            Cancel();
            if (Volatile.Read(ref _controlFailure) is { } failure)
                throw new InvalidOperationException("管线控制操作失败。", failure);
            throw;
        }
    }
    /// <summary>
    /// 修改当前设备的音量。
    /// </summary>
    public void SetVolume(double volume, bool muted) => _audioOutput?.SetVolume(volume, muted);
    /// <summary>
    /// 跳转时清除当前设备的旧采样。
    /// </summary>
    public void ClearAudio() => _audioOutput?.Clear();

    /// <summary>
    /// 看门狗请求安全退出，原生对象仍由工作线程归还。
    /// </summary>
    public void Fail(Exception error)
    {
        Interlocked.CompareExchange(ref _controlFailure, error, null);
        Cancel();
    }
    /// <summary>
    /// 立即中断所有线程，但不提前释放线程正在使用的原生资源。
    /// </summary>
    public void Cancel()
    {
        if (Interlocked.Exchange(ref _stopping, 1) != 0) return;
        SignalStop(_lifetime.Cancel);
        SignalStop(Demuxer.Stop);
        SignalStop(() => _video?.Dispose());
        SignalStop(() => _audio?.Dispose());
        SignalStop(_frames.Dispose);
    }
    /// <summary>
    /// 单个停止动作失败时仍通知其它任务退出。
    /// </summary>
    private void SignalStop(Action stop)
    {
        try { stop(); }
        catch (Exception exception)
        {
            FluxMediaLog.Write(LogLevel.Error, nameof(PlaybackPipeline), _instanceId, "Event=PipelineStopSignalFailed", exception);
        }
    }
    /// <summary>
    /// 等待全部线程退出后释放原生对象，多次调用共享结果。
    /// </summary>
    public ValueTask DisposeAsync()
    {
        lock (_stopSync)
        {
            Cancel();
            return new ValueTask(_stopTask ??= StopCoreAsync());
        }
    }
    /// <summary>
    /// 严格在线程退出后释放解码参数、音频设备和取消信号。
    /// </summary>
    private async Task StopCoreAsync()
    {
        try
        {
            var tasks = Completion is null ? _tasks.ToArray() : _tasks.Concat(new[] { Completion }).ToArray();
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            if (exception is not OperationCanceledException)
                FluxMediaLog.Write(LogLevel.Debug, nameof(PlaybackPipeline), _instanceId, "Event=PipelineStoppedAfterFault", exception);
        }
        finally
        {
            var errors = new List<Exception>();
            Release(_frames.Dispose);
            Release(() => _video?.Dispose());
            Release(() => _audio?.Dispose());
            Release(Demuxer.Dispose);
            Release(() => _audioOutput?.Dispose());
            Release(_lifetime.Dispose);
            if (errors.Count > 0) throw new AggregateException("播放管线资源释放失败。", errors);

            // 一项释放失败不影响后续资源清理。
            void Release(Action release)
            {
                try { release(); }
                catch (Exception exception) { errors.Add(exception); }
            }
        }
    }
}
