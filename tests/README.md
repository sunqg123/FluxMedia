# 测试

本目录包含核心功能测试和 Avalonia 消费项目。

| 项目 | 用途 |
| --- | --- |
| `FluxMedia.Tests` | 当前日志、生命周期、队列、帧租约、播放、录制与恢复用例 |
| `FluxMedia.Avalonia11.Compatibility` | 当前控件的 Avalonia 11 消费项目，覆盖声明的目标框架 |
| `FluxMedia.Avalonia12.Compatibility` | 当前控件的 Avalonia 12 消费项目 |

后两个项目用于消费者编译检查。
原生媒体用例需要本机部署匹配库和命令行工具，外部 HLS/RTSP 地址通过环境变量配置。

CI 为手动触发，测试需显式启用。
bin/obj/TestResults 不提交。原生库部署见 [FFmpeg 说明](../FFmpeg/README.md)。
Windows 本机默认目录为 `FFmpeg/win-x64/native`；其他部署使用 `FLUXMEDIA_FFMPEG_ROOT` 指定包含共享库及命令行工具的目录。
