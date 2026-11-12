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

## 诊断

启用日志后，可使用以下事件排查定位与显示问题：

- `MediaSeekPermission`：媒体时长与 Seek 权限。
- `SeekBindingSubmitted` / `SeekTrace`：目标提交、合并、定位与首帧。
- `SeekRejected`：不可定位媒体的请求拒绝。
- `SeekPacketGeneration` / `SeekFirstVideoPacket`：数据包代次和首包信息。
- `VideoTimelinePresented`：当前时间线的首次 Avalonia 显示提交。

`FirstUI` 表示核心视频帧回调，显示提交也不等于物理屏幕扫描时刻。
日志配置见 [日志说明](logging.md)。
