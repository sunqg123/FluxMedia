# API 使用

当前公开入口为 `FluxEngine`、`Player` 和可选 `FluxPlayerView`。

## 创建与关闭

```csharp
using FluxMedia.Playback;

FluxEngine.Start(new FluxEngineOptions
{
    FFmpegPath = Environment.GetEnvironmentVariable("FLUXMEDIA_FFMPEG_ROOT"),
});
await using var player = FluxEngine.CreatePlayer();
await player.OpenAndPlayAsync("video.mp4");
await player.PauseAsync();
await player.ResumeAsync();
await player.StopAsync();
```

`FluxEngine.Start` 是唯一的初始化入口，必须在创建播放器之前成功调用。
`FluxEngineOptions` 仅包含 `FFmpegPath` 和 `EnableLogging`。媒体超时等参数放在 `PlaybackOptions` 中。
缺少库、加载失败、ABI 不匹配或网络组件初始化失败均抛出异常，失败不会登记已启动状态。
`CreatePlayer` 不会隐式启动或重试初始化；未启动或正在关闭时抛出 `InvalidOperationException`。
重复启动可复用相同配置，传入不同配置会被拒绝。原生库路径只在引擎启动时配置，不属于媒体播放选项。
应用退出时调用 `FluxEngine.ShutdownAsync`。原生库和函数绑定保留到进程退出，切换库目录需重启进程。

## 自定义进度 UI

`Player.CanSeek` 为只读能力，`Player.IsLive` 为内容判断。
持续写入 `PositionSeconds` 可以提交点播拖动目标，无需等到松开。
直接调用 `SeekAsync` 或 `SeekByAsync` 同样经过核心权限检查。
直播返回 NotSeekable，绑定赋值也不会修改核心播放位置。
未知时长的网络源保守禁止 Seek，特殊流需根据元数据与日志进一步确认。

## 倍速播放

点播支持 0.5～2 倍速，可绑定 `Player.Speed` 或调用 `SetSpeedAsync`。
`Player.SupportedSpeeds` 提供常用档位，`Player.CanChangeSpeed` 用于控制 UI。

```csharp
if (player.CanChangeSpeed)
    await player.SetSpeedAsync(1.5);
```

音频通过 atempo 保持音调，视频跟随实际音频时钟；无可用音频设备时按媒体时间戳调度。
直播与未知时长媒体由核心拒绝变速，返回 `PlaybackRateNotSupported`，时序模式不能绕过权限。
打开新媒体按 `PlaybackOptions.InitialPlaybackRate` 初始化，直播固定为 1 倍速。
倍速只影响播放，录制保留源媒体的时间戳与时长。

## 视频帧

```csharp
player.VideoFrameReady += (_, args) =>
{
    using var lease = args.Frame.Retain();
    if (!lease.Frame.IsCurrent) return;
    // 在此同步消费；跨回调保存时，延后释放租约。
};
```

`ReadVideoFramesAsync` 支持异步消费。跨迭代保存也需要租约，异步处理后显示前再次检查 IsCurrent。
TimelineId 可关联同一次时间线的出帧与呈现日志。`FluxPlayerView.Player` 可直接绑定 Player。

## 录制与日志

```csharp
await player.StartRecordingAsync("recording.mp4");
await player.StopRecordingAsync();
```

录制是数据包重封装，输出容器需支持源编码。诊断从 Recording 和 GetRecordingDiagnosticInfo 读取。
日志使用 `FluxMedia.Core.Logging.FluxMediaLog`，见 [日志说明](logging.md)。
