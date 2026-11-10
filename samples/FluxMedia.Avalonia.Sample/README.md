# 项目引用示例

通过 ProjectReference 使用当前两个库，演示宿主配置和多播放器 UI。

## 目录结构

| 目录 | 职责 |
| --- | --- |
| `Models` | `PlayerInput` 保存媒体地址和录制路径等输入数据 |
| `Views` | 主窗口、独立播放器卡片，以及文件选择等界面操作 |
| `ViewModels` | 管理播放器集合、播放命令和界面状态 |
| `Commands` | 同步和异步命令实现 |
| `Infrastructure` | 属性变更通知基类 |

`App` 负责引擎初始化。`MainWindow` 将文件选择回调传给视图模型，视图模型不持有窗口。
`PlayerItemView` 继承列表项的数据上下文，输入绑定到 `Input`，播放属性绑定到核心 `Player`。

## 播放控制

进度条使用 PositionSeconds 双向即时绑定，CanSeek 控制交互，核心仍独立拦截直播 Seek。
倍速选择绑定 Speed 与 CanChangeSpeed，提供 0.5、0.75、1、1.25、1.5、2 倍速；直播禁用。

日志在运行输出目录的 Logs。Sample 的 ResolveFFmpegRoot 先检查 FLUXMEDIA_FFMPEG_ROOT，再从程序目录向父目录查找 `FFmpeg/<RID>/native`，通过 FFmpegPath 传入核心。
原生库不复制到 bin 或发布目录。核心只检查显式路径和环境变量，目录向上查找由 Sample 负责。
克隆仓库后执行 `git lfs pull`，获取 Windows x64 原生库和根目录的 Sample.mp4，也可自行选择媒体文件。

应用启动入口先调用 FluxEngine.Start，初始化失败直接抛出异常；播放器创建过程不再隐式启动引擎。
