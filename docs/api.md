# API 使用

当前公开入口为 `FluxEngine`、`Player`，Avalonia 可选择纯显示 `FluxPlayerView` 或完整操作控件 `FluxPlayerControl`。

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
`FluxEngineOptions` 配置 `FFmpegPath`、`EnableLogging` 和全局倍速范围。音频级数自动推算；媒体超时、初始倍速等参数放在 `PlaybackOptions` 中。
缺少库、加载失败、ABI 不匹配或网络组件初始化失败均抛出异常，失败不会登记已启动状态。
`CreatePlayer` 不会隐式启动或重试初始化；未启动或正在关闭时抛出 `InvalidOperationException`。
重复启动可复用相同配置，传入不同配置会被拒绝。原生库路径只在引擎启动时配置，不属于媒体播放选项。
应用退出时调用 `FluxEngine.ShutdownAsync`。原生库和函数绑定保留到进程退出，切换库目录需重启进程。

## 打开媒体

`OpenAsync` 和 `OpenAndPlayAsync` 的字符串入口统一识别本地路径与网络地址；
显式传入 `MediaSource` 或使用 `OpenUrlAsync` 也会经过核心校验。
本地输入检查文件是否存在，`file:` URI 转换为本地路径；Windows 盘符和共享路径按文件处理。
网络输入要求有效的绝对地址与主机，当前允许 `http`、`https`、`rtsp`、`rtmp`。
不支持的协议直接报错，`.m3u8` 后缀不改变协议支持范围。
打开失败通过 `PlaybackException` 和 `Faulted` 反馈；基础输入校验在结束旧媒体之前执行。

## 播放时序

`NetworkPlaybackOptions.TimingMode` 显式指定时优先生效。
`Auto` 下，本地文件和有明确时长的网络点播使用时间戳调度；
未知时长的网络直播根据 `LowLatencyMode` 选择低延迟或时间戳调度。
基础模式在媒体信息就绪后统一确定，解复用、视频调度和诊断使用同一结果。
倍速时调度器仍使用时间戳节奏；时序设置不改变直播的 Seek 和倍速限制。

## 自定义进度 UI

`Player.CanSeek` 为只读能力，`Player.IsLive` 为内容判断。
持续写入 `PositionSeconds` 可以提交点播拖动目标，无需等到松开。
直接调用 `SeekAsync` 或 `SeekByAsync` 同样经过核心权限检查。
直播返回 NotSeekable，绑定赋值也不会修改核心播放位置。
未知时长的网络源保守禁止 Seek，特殊流需根据元数据与日志进一步确认。

## 倍速播放

点播倍速由 `FluxEngineOptions.MinPlaybackRate` 和 `MaxPlaybackRate` 配置，默认范围为闭区间 0.1～10，所有播放器共用。
`FluxEngineOptions.MaxAudioTempoStages` 是只读计算属性，无需填写。它取上下限各自所需级数的较大值，默认 0.1～10 自动得到四级，0.1～30 得到五级，0.1～40 得到六级。
每级 atempo 覆盖 0.5～2 倍，N 级覆盖 `0.5^N～2^N`。
概念公式为 `max(ceil(log2(1 / Min)), ceil(log2(Max)))`，实际实现沿用建图的逐级切分规则，避免倒数溢出及对数取整误差。
范围为 1～1 时得到零级，输入直接进入格式转换。

| 自动级数 | 音频链可覆盖范围 |
| --- | --- |
| 4 | 0.0625～16 |
| 5 | 0.03125～32 |
| 6 | 0.015625～64 |

`FluxEngine.Start` 在初始化日志与原生库之前检查配置：上下限必须为有限值，满足 `0 < Min ≤ 1 ≤ Max`，然后按范围推算最大级数。
推算级数最多允许 16 级，对应 `2^-16～65536`。超过时直接抛出包含范围、所需级数和上限的配置异常，不创建滤镜；例如 `Min=1e-300` 会被拒绝。
在 Start 之前读取 MaxAudioTempoStages 时，无效范围也会抛出明确指出配置项的“倍速范围配置无效”异常；不返回可能被误认为有效计划的零级。
例如 `Min=0.1, Max=40` 自动使用六级，不再需要填写或匹配预算。
`Min=1.5, Max=2` 同样在 Start 失败，因为 1 倍必须始终是合法回退值。
启动后的配置保持不变；CreatePlayer 和 Open 不再提供另一套范围覆盖项。

```csharp
FluxEngine.Start(new FluxEngineOptions
{
    FFmpegPath = Environment.GetEnvironmentVariable("FLUXMEDIA_FFMPEG_ROOT"),
    MinPlaybackRate = 0.1,
    MaxPlaybackRate = 30
});
var player = FluxEngine.CreatePlayer();
```

纯视频同样遵守配置范围，只跳过单次请求的音频计划检查；音频计划和建图均使用由范围推算的最大级数。
`Player.DefaultSpeeds` 保持 0.5、0.75、1、1.25、1.5、2、3、4，是界面候选项，不是范围边界。
1.2.0 保留 `Player.SupportedSpeeds` 作为弃用别名，旧调用方仍可编译，后续请改用 `DefaultSpeeds`。
使用方可以维护自己的列表；`Player.CanChangeSpeed` 用于控制 UI。
绑定无效速度时会记录警告并恢复实际值；需要接收操作结果时调用 `SetSpeedAsync`。
已打开媒体的换速请求依次校验：正有限数值 → 配置范围 → 音频预算 → 媒体能力，失败统一返回 `PlaybackRateNotSupported`。
超出范围的提示包含上下限与当前值，例如使用默认配置时“播放速度需在 0.1 到 10 之间（当前 25）”，不会自动截取到边界。
直播在前三项通过后返回“直播不支持倍速”，未知时长媒体仍由核心拒绝。
请求采用“最后一个有效目标优先”：非法数值、越界或超预算的设置会失败，不隐含取消之前已经接受的目标。
新有效目标可以取代尚未提交的请求；已经提交中的请求会先完成，新目标随后处理。

```csharp
if (player.CanChangeSpeed)
{
    var result = await player.SetSpeedAsync(0.1);
    // IsSuccess && IsDeferred：已接受，等待启动或恢复后应用。
    // IsSuccess && !IsDeferred：当前处理链已经采用目标速度。
    // !IsSuccess：请求失败，IsDeferred 始终为 false。
}
```

音频预算预检与建图共用切分计划；1 倍时输入直接连接格式转换，不创建 atempo。
候选图构建失败时保留旧链，当前帧继续处理，失败通过操作结果或换速诊断报告，不触发播放器故障。
取得提交资格后才排旧链尾音并换图；提交中不接受普通超时或取消，提交失败按管线故障处理。
设备缓冲按各自倍速映射回媒体时间，因此 Applied 不表示设备已经播完旧倍速缓冲。
视频跟随实际音频时钟；无可用音频设备时按媒体时间戳调度，慢放等待期间持续检查倍速和暂停。
直播与未知时长媒体由核心拒绝变速，返回 `PlaybackRateNotSupported`，时序模式不能绕过权限。
打开新媒体按 `PlaybackOptions.InitialPlaybackRate` 初始化，直播固定为 1 倍速。
初始速度为非法数值、超出范围或超出预算时，记录包含原值、上下限和实际采用值 1 的警告并回退 1 倍，打开照常进行。
音频图尚未启动时，合法的初始自定义速度属于待应用请求。
打开成功不保证自定义初始倍速已经应用。后续候选图构建失败时保留基础音频链，可从换速诊断和 `RateBuildRejected` 日志查看原因。
基础音频链、解码器或设备初始化失败仍按播放故障处理，与候选变速图失败区分。
`PlaybackRateTimeout` 默认五秒，仅约束尚未提交且调用方仍在等待的请求。
`PlaybackRateCommitTimeout` 默认两秒，是可配置的提交看门狗，超时会记录 RateCommitWatchdog 并停止故障管线。
暂停或未启动时返回 Accepted 的请求不因等待时间过长而失败，Seek 会作废未提交请求并刷新绑定。
同媒体重连可保留最新未终结目标；停止、切源和释放会结束所有挂起请求。
取消令牌仅能作废提交前的普通等待请求；进入提交后，异步调用会等待应用结果或提交故障，不承诺取消即返回。
停止、切源或释放返回的 `OperationCanceled` 可表示“请求所属管线已退役”，不承诺旧管线从未短暂切换滤镜。
旧管线结果不会发布到新管线；看门狗不是强行中断原生调用或立即释放原生资源的机制。
`Player.Speed` 只发布已应用值，`RequestedSpeed`、`IsSpeedChangePending`、`LastSpeedError` 提供目标与操作状态。
诊断包含 `RequestedRate`、`AppliedRate`、`RateRequestId`、`RateChangeState`、`LastRateError`，以及当前配置的 `MinRate`、`MaxRate`；失败时请求值与应用值不等是正常的。
`RateChangeState` 和 `LastRateError` 属于最近登记的换速请求，不代表当前播放健康状态。
当前请求成功时清除其旧错误；较早请求的成功或失败不能覆盖较新请求的结果。
倍速只影响播放，录制保留源媒体的时间戳与时长。

1.2.0 行为变更：默认允许范围从任意正有限数值收紧到 0.1～10。此前低于 0.1 或超过 10 的调用会被拒绝；需要更大范围时在 FluxEngineOptions 中配置上下限，Start 校验通过后生效，音频级数自动匹配范围。

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
TimelineId 可关联同一次时间线的出帧与呈现日志。两个 Avalonia 控件的 `Player` 属性均可直接绑定 Player。
操作区模板及集合绑定见 [Avalonia 控件](avalonia-controls.md)。

## 录制与日志

```csharp
await player.StartRecordingAsync("recording.mp4");
await player.StopRecordingAsync();
```

录制是数据包重封装，输出容器需支持源编码。诊断从 Recording 和 GetRecordingDiagnosticInfo 读取。
同一播放器显式打开媒体时会结束旧录制，包括再次打开相同地址；内部网络恢复沿用录制转移流程。
录制收尾失败时保留失败诊断并发出 `Faulted`；打开新媒体和停止播放会在旧管线清理后抛出
`PlaybackException`，其 `PlaybackError` 为 `RecordingFailed`。切源不会继续打开新媒体并覆盖旧录制错误。
日志使用 `FluxMedia.Core.Logging.FluxMediaLog`，见 [日志说明](logging.md)。

停止录制的取消令牌在接受停止前生效；开始收尾后会等待排空、封装和释放，
返回实际文件结果，避免已完成的文件被标记为取消失败。
录制诊断的 `VideoCodec` 为源编码名称，未知时为空字符串。
`OutputFileFinalized` 表示文件尾写入和 IO 关闭均成功；实际文件录制未收到有效数据包时为 `Failed`。
文件大小查询失败不会改变录制结果，相关文件信息保留已有值。
成功完成时清空 `LastError`；停止时找不到应有的录制器则返回失败，诊断收敛为 `Failed`。

管线清理失败时，停止命令先进入 `Stopped`，再向调用方反馈清理异常；
新管线打开失败时先进入 `Failed`，异常同时保留打开和清理失败的信息。

`GetDiagnosticInfo().LastSeek.ActualPosition` 为跳转后首个有效输出帧的时间戳，
尚无输出帧或跳转到媒体末尾而未出帧时为空。
网络诊断中的包计数针对当前或最近一代管线：`Dropped*PacketCount` 表示缓冲策略丢弃，
`SeekDiscarded*PacketCount` 和 `StopDiscarded*PacketCount` 分别表示主动 Seek 与停止清理。
