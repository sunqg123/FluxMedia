# 当前测试源码

本目录保留当前实现的用例，不保留已删除组件的孤立测试。
当前最新 Seek 修改尚未执行构建或测试；历史通过次数不适用于当前源码。

| 项目 | 用途 |
| --- | --- |
| `FluxMedia.Tests` | 当前日志、生命周期、队列、帧租约、播放、录制与恢复用例 |
| `FluxMedia.Avalonia11.Compatibility` | 当前控件的 Avalonia 11 消费项目，覆盖声明的目标框架 |
| `FluxMedia.Avalonia12.Compatibility` | 当前控件的 Avalonia 12 消费项目 |

后两个项目是消费者编译检查，不是旧媒体框架兼容转发层，也不能代替实机显示验证。
原生媒体用例需要本机部署匹配库和命令行工具，外部 HLS/RTSP 地址通过环境变量配置。
未部署依赖或未执行的用例不能当作真实媒体验证通过。

只有用户明确要求后才运行测试或 CI。CI 为手动触发，测试还需明确启用。
bin/obj/TestResults 不提交。原生库部署见 [FFmpeg 说明](../FFmpeg/README.md)。
Windows 本机默认目录为 `FFmpeg/win-x64/native`；其他部署使用 `FLUXMEDIA_FFMPEG_ROOT` 指定包含共享库及命令行工具的目录。
