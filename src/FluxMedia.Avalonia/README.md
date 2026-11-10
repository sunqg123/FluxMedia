# FluxMedia.Avalonia

`FluxMedia.Avalonia` 提供播放器控件和视频显示，目标框架为 `netstandard2.1`。
Avalonia 11/12 的消费项目保存在 `tests`。

应用通过 `FluxEngine.Start(...)` 统一配置 FFmpeg 和日志。
媒体源需要单独配置时，再向 `OpenAsync` 或 `OpenAndPlayAsync` 传入 `PlaybackOptions`。
自定义进度条可绑定 `Player.CanSeek`；直播限制同时由核心执行。

接入示例见仓库的 `samples` 目录。
