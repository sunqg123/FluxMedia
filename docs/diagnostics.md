# 诊断边界

`FluxMedia.Playback/Diagnostics` 与 `Internal` 同级，包含诊断模型、Player 查询入口及按需投影实现。
正常播放文件不引用诊断类型，不向诊断发送专用回调；诊断不提交控制命令、维护历史或写入健康日志。
FFmpeg 参数描述只在诊断侧缓存最近一次媒体和配置的结果；身份不变时复用，查询新媒体或新配置时替换，不在打开或解码路径维护缓存。

## 保留的数据

| 模型 | 内容 |
| --- | --- |
| PlaybackDiagnosticInfo | 本次读取的状态、媒体、配置、位置、缓冲、音频时钟和倍速请求结果 |
| PlaybackNetworkDiagnosticInfo | 重连配置、当前读取任务状态和读取错误 |
| PlaybackSeekDiagnosticInfo | 执行路径、合并数量，以及控制策略需要的定位和首帧耗时 |
| PlaybackRecordingDiagnosticInfo | 实际录制结果、成功写入包数及本次查询的文件属性 |
| PlaybackHealthDiagnosticInfo | 已确认的播放失败及可读取的缓冲信息，不进行健康轮询 |

没有数据来源的计数、阶段耗时和硬解执行详情字段已删除；同时删除 Lifecycle、Trace 及其专用类型。
录制开始/结束墙钟、录制时长、累计网络尝试次数、Seek 阶段轨迹不再提供。
保留成员中的 null 表示当前事实不可读取，不代表 0、false、正常或软件解码。
`HardwareAccelerationRequested` 只表示配置意图；`Health.IsHealthy` 只在已进入 Failed 时为 false，其他情况不作健康判定。
音频时钟不可用时，不以时钟的缺省缓冲值表示设备已清空。

## 查询一致性

查询先捕获现有的管线、媒体、配置、操作生命周期和同步请求，再校验读取前后的身份。
管线还会核对自身创建时的来源和配置。诊断不增加专用代次，不等待播放命令锁。
打开或停止期间直接返回 null；读取中检测到变化最多重读一次，仍无法稳定读取则返回 null。
每次成功查询的数据属于同一媒体上下文；它不是所有工作线程数值在同一瞬间的事务快照，也不保证多次独立查询之间无状态变化。

`GetDiagnosticInfo()`、`GetRecordingDiagnosticInfo()` 和 `Player.Recording` 均可能为 null。
`PlaybackDiagnosticInfo.State` 与本次快照一起读取；显示快照时应使用它，避免再读取后续时刻的 Player.State。
`IsSpeedChangePending` 与本次倍速结果一起读取，界面无需解析状态字符串或再次查询播放器。

```csharp
var diagnostic = player.GetDiagnosticInfo();
if (diagnostic is not null)
{
    Console.WriteLine(diagnostic.State);
}
```

录制文件属性仅在显式查询时读取；文件读取失败时属性为空，不影响录制结果。
文件系统可能被其他进程修改，文件属性表示读取时所见的路径状态。
PlayerBinding 的 RecordingState 和 IsRecording 按实际变化通知；Recording 在媒体、录制状态或媒体切换的诊断可用性变化时通知。
录制中的包数、文件大小和错误可能在状态不变时更新，持续显示这些数据应由使用方按需调用 GetRecordingDiagnosticInfo；音量和倍速操作不触发录制诊断刷新。
Seek 首帧任务退出后不保留额外历史，已无法读取的首帧位置和时间点为 null。

## API 调整

删除不再提供的字段和生命周期/轨迹类型，不保留兼容转发；调用方需要同步修改旧字段访问和空值处理。
业务结果继续使用操作返回值、Player.State、RecordingState 和 Faulted，不以诊断缺省值决定播放行为。
Avalonia 的渲染诊断由显示控件单独提供，不进入播放核心，也不作为核心同步时钟。
