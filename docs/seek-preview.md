# Seek 与进度条

## 核心权限

使用 `Player.CanSeek` 控制进度条交互，`Player.IsLive` 表示直播状态。
直播的跳转请求由核心拒绝，自定义 UI 也不能绕过。

当前以有限媒体总时长判断 Seek 能力，不单凭 RTSP、HTTP 或文件扩展名判断。
无已知总时长的媒体不开放 Seek；带回看窗口的直播需要结合源元数据判断。

## 进度条绑定

Avalonia 控制栏将进度条与 `PlayerBinding.PositionSeconds` 双向绑定，由绑定赋值提交拖动目标，
并通过绑定对象读取核心 CanSeek 控制是否可调整。适配器由 FluxPlayerControl 管理，可通过 Presentation 访问。
拖动预览保存在绑定对象中，Player.Position 与 PositionChanged 继续报告核心位置。

自定义 UI 应区分程序回写和交互赋值，避免播放进度刷新产生新的 Seek。
也可以调用 `SeekAsync` 或 `SeekByAsync`，两者都经过核心权限检查。
连续请求会合并待执行目标；长 GOP、解码预滚和管线重建会影响响应时间。

## 执行与诊断边界

核心使用类型化执行路径决定原地定位、重建或首帧恢复，不读取诊断字符串。
自适应合并保留正常控制需要的定位耗时、首帧等待时间、合并数量和整体耗时。
首帧仍由 PlaybackSeekFrame 与帧代次确认，移除诊断不会改变首帧等待与恢复逻辑。

诊断仅投影当前仍存在的执行状态和首帧任务，不保留 SeekTrace 环形队列、历史阶段日志或首帧历史。
无法读取的字段为 null；核心首帧回调不代表 Avalonia 已提交合成，更不代表屏幕呈现。
详见 [诊断边界](diagnostics.md)。
