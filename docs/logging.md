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

## 输出格式

文件和控制台使用相同的日志头：

```text
yyyy-MM-dd HH:mm:ss.fff [级别] [模块] SessionId=播放器标识 ThreadId=线程标识 Event=事件 参数...
```

`Trace`、`Debug`、`Info`、`Warning`、`Error`、`Critical` 分别显示，不合并级别。
没有播放器标识时，`SessionId` 显示为 `-`。

以 `Event=` 开头的消息保留事件及参数；普通说明写入 `Event=Message Message="..."`，不从标点猜测事件名。
消息中的换行和制表符转义为 `\r`、`\n`、`\t`，避免一条事件被拆成多条日志。
异常类型、内部异常和完整堆栈在下一行缩进显示，不再重复拼接异常说明。

播放控件的失败日志包含 `Operation`，可区分播放/暂停、停止、快进和后退。
宿主通过 `SetSink` 接管日志时，仍接收原始参数，可自行决定输出格式。

## 播放错误与诊断

正常播放保留必要的拒绝、故障、回退和清理错误日志。
核心不再维护 SeekTrace、首包跟踪、阶段耗时日志或健康轮询日志。
Sample 的绑定提交日志和 Avalonia 显示日志由各自项目决定，不作为核心播放控制的输入。

诊断只读取已有状态；缺失指标不靠额外日志或正常流程统计补齐，详见 [诊断边界](diagnostics.md)。
读取或分享日志时应过滤源认证信息，原始运行日志不提交到 Git。
