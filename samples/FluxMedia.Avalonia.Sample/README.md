# 项目引用示例

通过 ProjectReference 使用当前两个库，演示宿主配置和多播放器 UI。
进度条使用 PositionSeconds 双向即时绑定，CanSeek 控制交互，核心仍独立拦截直播 Seek。

日志在运行输出目录的 Logs，FFmpeg 目录由环境变量或仓库 FFmpeg 目录提供。
克隆仓库后执行 `git lfs pull`，获取 Windows x64 原生库和根目录的 Sample.mp4，也可自行选择媒体文件。
