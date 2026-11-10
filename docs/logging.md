# 静态日志

日志实现统一位于 `src/FluxMedia.Playback/Logging`，业务组件不注入日志接口。
入口为 `FluxMedia.Core.Logging.FluxMediaLog`。

## 配置

```csharp
using FluxMedia.Playback;
using FluxMedia.Core.Logging;

FluxEngine.Start(new FluxEngineOptions
{
    FFmpegPath = Environment.GetEnvironmentVariable("FLUXMEDIA_FFMPEG_ROOT"),
    EnableLogging = true
});
FluxMediaLog.Info("Application", "播放器已启动");
```

引擎通过 `EnableLogging` 控制日志，默认关闭。开启后使用程序输出目录下的 Logs 和默认日志级别，不需要传入目录。
Avalonia 示例显式开启日志。
实际文件路径由 FluxMediaLog.CurrentLogFilePath 或 FluxEngine.CurrentLogFilePath 读取。

宿主也可使用 Configure、UseFile、UseConsole 或 SetSink 设置自己的目标。
宿主替换输出后，引擎关闭不会误关新的宿主目标。宿主自行配置时负责退出清理。
文件输出使用队列并周期写入；切换输出、回调异常和关闭由日志目标生命周期处理。

## Seek 诊断

- MediaSeekPermission：内容判断与 Seek 权限。
- SeekBindingSubmitted / SeekTrace：提交、合并、定位、首帧及结束。
- SeekRejected 等拒绝事件：直播或不可定位源的后端拦截。
- SeekPacketGeneration / SeekFirstVideoPacket：包代次及首包关键帧标记。
- SeekFrameScheduling：定位首帧的调度策略。
- VideoTimelinePresented：同一 TimelineId 的首次 Avalonia 显示提交。

FirstUI 是核心帧回调，不是物理显示时刻。
读取或分享日志时应过滤源认证信息，原始运行日志不提交到 Git。
