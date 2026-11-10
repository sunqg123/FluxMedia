using FluxMedia.Core.Configuration;
using FluxMedia.Core.Logging;
using FluxMedia.Core.Models;
using FluxMedia.FFmpeg.Pipeline;
using FluxMedia.Playback.Internal.Audio;

namespace FluxMedia.Playback.Internal;

/// <summary>
/// 独占一次播放管线的线程、取消信号和原生资源。
/// </summary>
internal sealed class PlaybackPipeline : IAsyncDisposable
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
    /// 唯一的资源释放任务。
    /// </summary>
    private Task? _stopTask;
    /// <summary>
    /// 是否已发出停止信号。
    /// </summary>
    private int _stopping;

    /// <summary>
    /// 创建独立管线，打开、重播、跳转重建和重连均使用此入口。
    /// </summary>
    public PlaybackPipeline(MediaSource source, PlaybackOptions options, Guid instanceId, TimeSpan position, PlaybackVideoFrameHub output, PlaybackPipelineCallbacks callbacks, CancellationToken operationToken)
    {
        _source = source;
        _options = options;
        _instanceId = instanceId;
        _startPosition = position;
        _callbacks = callbacks;
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(operationToken);
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
    public bool DemuxerExited => _tasks.Count > 0 && _tasks[0].IsCompleted;
    /// <summary>
    /// 管线是否已被取消。
    /// </summary>
    public bool IsCanceled => Volatile.Read(ref _stopping) != 0 || _lifetime.IsCancellationRequested;
    /// <summary>
    /// 最新音频时钟，仅属于当前管线。
    /// </summary>
    public AudioPlaybackDiagnostics AudioDiagnostics { get; private set; } = new();
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
        if (_tasks.Count != 0) throw new InvalidOperationException("管线只能打开一次。");
        var demuxTask = Demuxer.StartAsync(_lifetime.Token);
        _tasks.Add(demuxTask);
        _ = ObserveDemuxerAsync(demuxTask);
        await Demuxer.WaitUntilReadyAsync(cancellationToken).ConfigureAwait(false);
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
        if (Demuxer.AudioStream is not null)
        {
            _audioOutput = AudioOutputDeviceFactory.Create(_instanceId);
            _audio = new FFmpegPacketAudioPlaybackWorker(_source, _instanceId, _startPosition, Demuxer.AudioStream, Demuxer.AudioPackets, () => Demuxer.PacketGeneration,
                _audioOutput, _callbacks.IsPaused, _callbacks.PlaybackRate, _callbacks.Volume, _callbacks.IsMuted, UpdateAudioDiagnostics,
                _callbacks.Resync, packet => Demuxer.WriteRecordingPacket(packet));
        }
        _video = new FFmpegPacketVideoPlaybackWorker(_source, _options, _instanceId, _startPosition, Demuxer.VideoStream, Demuxer.VideoPackets, () => Demuxer.PacketGeneration,
            _frames.Queue, _callbacks.IsPaused, _callbacks.PlaybackRate, _callbacks.VideoDiagnostics,
            () => AudioDiagnostics, () => Demuxer.VideoBufferedDuration, () => Demuxer.VideoPacketCount,
            _callbacks.Resync, _callbacks.VideoCapabilities, packet => Demuxer.WriteRecordingPacket(packet));
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
        catch { _video?.Dispose(); _audio?.Dispose(); }
    }
    /// <summary>
    /// 先排空解码器及尾帧，再完成整个管线。
    /// </summary>
    private async Task RunToCompletionAsync(Task videoTask, Task audioTask, Task pumpTask)
    {
        try
        {
            // 任一输出任务失败时立即结束，不能让其它线程继续向失效输出写入。
            var observed = new List<Task> { videoTask, audioTask, pumpTask };
            while (!videoTask.IsCompleted)
            {
                var first = await Task.WhenAny(observed).ConfigureAwait(false);
                await first.ConfigureAwait(false);
                observed.Remove(first);
            }
            await videoTask.ConfigureAwait(false);
            await _tasks[0].ConfigureAwait(false);
            _lifetime.Token.ThrowIfCancellationRequested();
            _frames.Complete();
            await Task.WhenAll(audioTask, pumpTask).ConfigureAwait(false);
        }
        catch
        {
            Cancel();
            throw;
        }
    }
    /// <summary>
    /// 更新独立音频时钟后通知所属会话。
    /// </summary>
    private void UpdateAudioDiagnostics(AudioPlaybackDiagnostics diagnostic)
    {
        AudioDiagnostics = diagnostic;
        _callbacks.AudioDiagnostics(diagnostic);
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
    /// 立即中断所有线程，但不提前释放线程正在使用的原生资源。
    /// </summary>
    public void Cancel()
    {
        if (Interlocked.Exchange(ref _stopping, 1) != 0) return;
        _lifetime.Cancel();
        Demuxer.Stop();
        _video?.Dispose();
        _audio?.Dispose();
        _frames.Dispose();
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
            _frames.Dispose();
            _video?.Dispose();
            _audio?.Dispose();
            Demuxer.Dispose();
            _audioOutput?.Dispose();
            _lifetime.Dispose();
        }
    }
}
