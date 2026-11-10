# FluxMedia.Avalonia

`FluxMedia.Avalonia` 提供播放器控件和视频显示，目标框架为 `netstandard2.1`。
Avalonia 11/12 的消费项目保存在 `tests`；项目存在不代表最新源码已通过编译或实机验证。

应用通过 `FluxEngine.Start(...)` 统一配置 FFmpeg 和日志。
媒体源需要单独配置时，再向 `OpenAsync` 或 `OpenAndPlayAsync` 传入 `PlaybackOptions`。
自定义进度条可绑定 `Player.CanSeek`；直播限制同时由核心执行。

最新 Seek 和显示代次修改尚未构建、测试。当前示例见 `samples`，
实现范围及未完成项见仓库的 `docs/current-state.md`。
