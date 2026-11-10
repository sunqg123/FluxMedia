# 点播拖动预览与直播 Seek 限制

日期：2026-11-09。状态：源码已修改，未构建、未测试、未启动播放器。
本轮按用户最新要求区分直播与点播，不按 RTSP、HTTP 或本地文件名直接限制协议。

## 核心权限

- `Player.CanSeek`、`Player.IsLive` 提供只读状态，供自定义 UI 使用。
- `PlaybackSession.SeekAsync` 在排队前检查权限；执行任务拿到管线锁后再次检查。
- `Player.Position` / `PositionSeconds` 的绑定赋值在禁止 Seek 时不改写核心位置。
- `SeekByAsync` 仍进入同一个核心 Seek 入口，不能绕过权限。
- 解复用器的排队及原生定位入口同样检查点播权限；队列原地定位路径已移除。
- 改成时间戳输出模式不会让直播获得 Seek 权限。

当前依据实际读取的有限媒体总时长：有有限总时长的点播可 Seek，
无有限总时长的网络源保守按直播处理。RTSP 点播不因协议被拒绝，HTTP 直播也不会因 URL 后缀获准。
本地未识别总时长的媒体也不开放时间跳转。

时长推断不是所有网络协议的绝对内容识别方式。带回看窗口的直播或特殊动态清单，
需要结合 `MediaSeekPermission` 日志核对元数据，再决定是否补充明确的原生能力证据。
当前没有宣称已验证所有协议的分类。

## 连续预览修改

DLL Demo 原本只在 PointerReleased 调用 Seek，现在在滑块值变化时提交 Player.PositionSeconds。
程序回写播放进度使用单独标记，不会生成新的 Seek；键盘调整复用同一入口。
指针事件使用隧道路由接收 Thumb 已处理的事件，松开不再重复提交最终目标。
拖动或等待 Seek 时保留用户滑块目标，避免 500ms 诊断刷新抢回进度条。

Sample 的进度条改为显式 TwoWay / PropertyChanged，并绑定 Player.CanSeek；
前后跳转按钮也使用相同只读权限。

调度器只保留一组待执行的最新目标，排队请求不再覆盖正在执行的 RequestId 与首帧目标。
Trace 记录独立存储，不再把旧诊断快照写回正在更新的首帧状态。
连续跳转按启动时间控制间隔：默认 33ms，范围 16–66ms；执行已耗时较长时不再额外等待一次完整间隔。
这些数字是当前实现参数，不是已测得的预览响应时间。
长 GOP、EOF 后重建、硬件初始化等剩余开销仍需用户操作后的日志定位。

## 用户操作后的日志

DLL Demo 和 Sample 都配置 `EnableLogging=true`，目录为程序输出目录下的 `Logs`。
核心日志文件路径可从 Demo 诊断面板查看；自定义宿主需自行启用 FluxEngine 日志。
默认库行为仍由宿主选取，不强制开启全局日志。

关注事件顺序：

1. `MediaSeekPermission`：打开后识别的 IsLive、CanSeek、总时长及源类型。
2. `SeekDrag`：Demo 的拖动开始、松开或捕获丢失，附显示提交计数。
3. `SeekBindingSubmitted`：目标进入 Player 绑定入口。
4. `SeekTrace` 的 Queued / CoalescedExecute / FirstUI / Completed：合并、执行与首帧耗时。
5. `SeekRejected` / `SeekExecutionRejected` / `SeekBindingRejected`：直播或不可定位源的核心拒绝。

先用最新源码启动程序，再操作本地或网络点播的拖动、暂停后拖动及直播的直接 Seek。
由用户复现后再读日志；这不是已执行的测试步骤。
`FirstUI` 当前表示核心视频帧回调，不等于物理屏幕显示时刻。
新增日志不记录源地址；已有日志可能包含地址，读取和转述时应过滤认证信息。

本机最新脱敏诊断材料：`artifacts/diagnostics/seek-20261109/`，不提交到 Git。

后续用户操作已暴露旧帧误判及启动缓冲等待，追加源码修正和证据见 [日志分析](seek-log-analysis.md)。当前新增修改尚未构建、未测试。
