# 日志

日志直接使用 `LogConverge.Sharp.Log`，不再维护单独的日志配置、队列、输出端和格式化实现。

## 初始化

```csharp
using FluxMedia.Playback;
using LogConverge.Sharp;

FluxEngine.Start(new FluxEngineOptions
{
    FFmpegPath = Environment.GetEnvironmentVariable("FLUXMEDIA_FFMPEG_ROOT"),
    EnableLogging = true
});
Log.Info("Application", "播放器已启动");
```

`EnableLogging` 默认关闭。`Start` 调用 `Log.Initialize`，默认目录为
`Path.Combine(AppContext.BaseDirectory, "Logs")`。返回 `Ok` 或 `AlreadyInitialized`
才继续初始化 FFmpeg，其余结果直接抛出初始化异常。

日志配置是进程级的，以首次成功初始化为准，关闭模式也属于初始化。
宿主如需自定义目录，应在 `FluxEngine.Start` 前自行调用 `Log.Initialize(directory, enabled)`
并检查结果；引擎会沿用已有实例。运行期间或引擎重新启动时，不会切换目录或开关。
FFmpeg 初始化失败也不会销毁共享日志，修正 FFmpeg 配置后可重试。

`FluxEngine.Shutdown` 只关闭播放资源，不销毁宿主可能共用的日志。
日志包在正常进程退出时自动排空并释放；强制终止或崩溃不能保证最后的记录落盘。
文件名、滚动、格式和后台刷新均由 LogConverge 负责。

当前依赖版本为 `0.7.0`，随包提供 Windows x64 原生日志库，启用时自动加载，
不需要另行复制 DLL。其他平台可关闭日志使用播放核心；开启日志时若平台不受支持，
`Start` 会因日志初始化失败抛出异常。

## 记录范围

- 引擎启动、关闭、FFmpeg 就绪和媒体打开。
- 录制开始、完成和失败。
- 网络重连尝试及恢复，硬件或显示路径的关键回退。
- 最终播放故障、订阅回调异常，以及无法继续向上抛出的清理错误。

不再记录每次状态切换、Seek 提交或成功定位、逐个动态库加载、录制分段、
每帧显示或逐帧转换失败。同一条异常优先由最终处理边界记录；被恢复的设备错误仍保留记录。
播放器相关消息携带 `SessionId`，事件使用 `Event=...`，具体日志头由 LogConverge 格式化。
诊断只按需读取已有状态，见 [诊断边界](diagnostics.md)。

## 调用方迁移

将原 `FluxMediaLog` 调用替换为 `Log.Info`、`Log.Warning`、`Log.Error` 等直接调用。
需要为警告附带异常时，使用 `Log.Write(LogLevel.Warning, category, message, exception)`。
`Write` 返回日志结果，不将写入失败抛到播放流程。

原 `LoggingOptions`、`LogFileRollingMode`、自有 `LogLevel`、日志输出端和
`FluxEngine.LogDirectory/CurrentLogFilePath` 已移除，不提供兼容转发。
目录由首次 `Log.Initialize` 的调用方掌握，文件命名遵循日志包规则。
日志可能包含路径及异常详情，分享前请过滤认证信息；原始运行日志不提交到 Git。
