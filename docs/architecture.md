# 当前架构

公开创建入口为 `FluxEngine.CreatePlayer`。业务控制走 `Player`，原生线程、设备及会话为内部实现。

```mermaid
flowchart TD
    Engine[FluxEngine] --> Player[Player]
    Player --> Session[PlaybackSession]
    Session --> Seek[PlaybackSeekController]
    Session --> SeekFrame[PlaybackSeekFrame]
    Session --> Recording[PlaybackRecordingController]
    Session --> Pipeline[PlaybackPipeline]
    Pipeline --> Demux[FFmpegSharedDemuxer]
    Pipeline --> Workers[视频与音频解码]
    Workers --> Pump[PlaybackFramePump]
    Pump --> Hub[PlaybackVideoFrameHub]
    Hub --> View[FluxPlayerView 或自定义消费者]
    Control[FluxPlayerControl] --> View
    Control --> Player
    Control --> Binding[PlayerBinding]
    Binding --> Player
```

`FluxPlayerControl` 组合显示层、基础命令和使用方提供的控制栏模板。它只借用外部 Player，
显隐、输入和错误提示留在 Avalonia 层，全屏由宿主响应请求。
Player 只提供核心状态、异步命令与普通事件；不承担 UI 线程派发、属性通知和时间文本。
Avalonia 的 Bindings/PlayerBinding 处理双向绑定与进度预览，控件通过 Presentation 暴露当前适配器。
适配器不参与解码或视频帧传递，卸载时解除订阅而不释放外部播放器。
诊断按需读取现有状态；播放不依赖诊断模型，不进行健康轮询或专用阶段统计。

## 源码职责目录

`FluxMedia.Playback` 根目录提供 `FluxEngine`、`Player` 和 `FFmpegLibraries` 公开入口。
内部实现按职责组织；目录与命名空间分别表达源码位置和类型身份。
独立类型以类型名分别存放；较大的私有辅助类型通过 `partial` 分文件，保留原有嵌套作用域。

会话的按需诊断投影集中在 [PlaybackSession.Diagnostics.cs](../src/FluxMedia.Playback/Diagnostics/PlaybackSession.Diagnostics.cs)。
解复用器的录制借用关系与预录包缓存集中在 [FFmpegSharedDemuxer.Recording.cs](../src/FluxMedia.Playback/Internal/Demuxing/FFmpegSharedDemuxer.Recording.cs)；录制控制器持有录制器，写入线程负责容器收尾。
两者均为原类型的 `partial` 文件。诊断部分只读取正常播放已有的事实，不维护控制状态。

| 目录 | 职责 |
| --- | --- |
| [Sessions](../src/FluxMedia.Playback/Internal/Sessions) | 会话生命周期、状态转换、控制协调和位置通知 |
| [Pipeline](../src/FluxMedia.Playback/Internal/Pipeline) | 一代播放管线的任务、资源与回调 |
| [Demuxing](../src/FluxMedia.Playback/Internal/Demuxing) | 打开参数、媒体信息、流描述与解复用 |
| [Packets](../src/FluxMedia.Playback/Internal/Packets) | 压缩包租约、队列与读取 |
| [Decoding](../src/FluxMedia.Playback/Internal/Decoding) | 音视频解码工作器 |
| [Timing](../src/FluxMedia.Playback/Internal/Timing) | 音视频同步、呈现节奏、音频时间线和倍速策略 |
| [Video](../src/FluxMedia.Playback/Internal/Video) | 视频帧池、帧泵和公开帧分发 |
| [Seeking](../src/FluxMedia.Playback/Internal/Seeking) | Seek 合并、重同步与本代首帧等待 |
| [Recording](../src/FluxMedia.Playback/Internal/Recording) | 录制控制与压缩包重封装 |
| [Audio](../src/FluxMedia.Playback/Internal/Audio) | 音频设备和变速滤镜 |
| [FFmpeg/Native](../src/FluxMedia.Playback/Internal/FFmpeg/Native) | 原生库加载、初始化与 FFmpeg 错误转换 |
| [Hardware](../src/FluxMedia.Playback/Internal/Hardware) | 硬件视频上下文 |
| [Diagnostics](../src/FluxMedia.Playback/Diagnostics) | 按需读取已有事实，存放诊断模型与查询实现 |

配置与公开业务数据分别位于 `Configuration`、`Models`；诊断类型与读取实现统一位于同级 `Diagnostics`。日志直接使用 LogConverge.Sharp。
Avalonia 显示位于独立的 `FluxMedia.Avalonia` 项目，自定义 UI 可直接使用核心帧输出。

媒体源规则集中在 `Demuxing/MediaSourcePolicy`：公开字符串入口调用地址解析，
会话在打开前统一校验媒体类型、协议、主机与文件存在性。
`PlaybackFFmpegOptions` 按协议生成参数，诊断部分按需从同一生成规则读取配置说明。
网络打开与读取直接采用 Network.OpenTimeout / ReadTimeout；FFmpeg 原生退避上限和核心重试间隔分别由 FFmpegReconnectDelayMax、ReconnectDelay 管理。
Seek 能力和倍速范围分别由 `PlaybackTimingPolicy`、`PlaybackRatePolicy` 维护。
基础时序由 `PlaybackTimingPolicy` 在流信息就绪后确定，保存在解复用器中，
再通过管线的工作上下文交给视频 Worker，避免读取和呈现各自判定媒体模式。

解码工作器通过 `PlaybackWorkerContext` 共享媒体信息及实时控制入口，
通过 `FFmpegPacketInput` 接收流参数、包读取器和代次；音视频专用回调分别组织。
这些依赖对象在每代管线启动时创建，资源仍由管线管理。
工作器直接持有依赖分组，实时读取播放控制；视频调度通过 AudioClockSnapshot 获取可听位置、倍速和缓冲时长，
不读取音频诊断。音频设备的暂停和恢复由工作器内同一方法处理。

视频工作器的主循环负责读包、Seek 同步、暂停和帧调度；硬件配置、解码器打开、帧转换与交付、资源清理由私有方法分别处理。
原生资源由本次运行持有并在外层 finally 中统一清理；不为了观测维护局部计数或阶段计时。

## 生命周期

每代管线只运行一次，独占解复用器、解码器、设备、取消信号和输入帧队列。
打开、重播、Seek 重建和网络恢复共用管线创建与退役流程。
停止先取消请求并等待线程退出，再释放原生对象；引擎关闭等待播放器释放。
改变管线的操作共用会话异步锁，旧管线通知不会修改新管线状态。
管线持续观察解复用、解码及帧泵任务，任一失败即取消其余任务。
正常结束时先排空解码数据，再完成帧泵，等待尾帧和尾音结束。
音视频工作器通过任务和操作结果传递错误；重连直接读取原生读取失败状态，不读取诊断字符串。
释放阶段逐项执行清理并汇总异常，单项失败不跳过其余资源。
退役返回清理结果，由命令层完成状态迁移后反馈异常，避免停留在过渡状态。

## Seek 与画面

权限独立于输出时序选项，核心会话和解复用入口均进行检查。
请求只保留一组最新待执行目标，执行中的目标不被排队请求覆盖。
正常定位由现有解复用器执行，失败或 EOF 后才重建，不再改写压缩包队列进行定位。

数据包标记原生读取代次，解码器据此丢弃旧包并保留新位置首包。
VideoFrameEpoch 在跳转开始或管线退役时失效，帧泵、公开读取和显示前都会检查。
PlaybackSeekFrame 只接收本次代次的首帧，使用任务通知而非轮询诊断字段。
目标首帧直接用于预览，再恢复常规音视频调度。
待处理 Seek 可通过 FFmpeg 中断回调结束读包等待；打开和原生定位阶段不启用该条件。
主动中断不计为网络超时，协议无法继续定位时沿用管线重建路径。
Seek 实际位置来自本代首个有效输出帧，原生定位完成只表示请求已执行。

## 输出与录制

VideoFrame.Retain 保留帧租约，消费方负责释放。异步显示前可再次检查 IsCurrent。
TimelineId 关联核心回调和显示提交；显示提交不等于物理屏幕扫描时刻。
Windows 显示实现包含 CPU 位图与 D3D11 共享纹理路径，其它平台需分别确认。
录制复用压缩包，控制器是录制器的唯一所有者，解复用器仅借用它投包；不负责修复上游码流损坏。
停止录制先解除投包关联，再异步等待写入线程排空、封装和释放。
队列满、包复制或容器写入失败会终止本次录制，最终结果保留失败；失败实例释放后可重新开始。
录制终态通过控制器转发到 Player.PlaybackChanged，不依赖诊断查询或播放进度；通知在写入线程之外执行。
接受停止前允许取消，开始收尾后以最终封装结果为准。
录制分别判断封装、关闭和有效数据，文件信息查询不参与判定录制成败。

## 日志与依赖

日志直接使用 LogConverge.Sharp.Log，在 FluxEngine.Start 初始化；输出和进程退出清理由日志包管理。
FFmpegLibraries 从绑定常量生成平台库名，并检查完整目录及实际 ABI。
任意主版本支持尚未实现，直接原生结构布局仍是限制，见 [部署说明](ffmpeg-deployment.md)。
Shared 中的 IsExternalInit 是 netstandard2.1 的 record/init 编译标记，不是业务兼容层。

诊断能力和空值约定见 [诊断边界](diagnostics.md)。

诊断查询校验读取前后的既有媒体和管线身份，切换中无法稳定读取时返回 null；不新增观测代次或命令锁等待。
