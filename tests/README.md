# 测试

本目录包含核心功能测试和 Avalonia 消费项目。

| 项目 | 用途 |
| --- | --- |
| `FluxMedia.Tests` | 日志、生命周期、队列、帧租约、帧调度、网络取消与外部流用例 |
| `FluxMedia.Avalonia11.Compatibility` | 当前控件的 Avalonia 11 消费项目，覆盖声明的目标框架 |
| `FluxMedia.Avalonia12.Compatibility` | 当前控件的 Avalonia 12 消费项目 |

后两个项目用于消费者编译检查。
原生媒体用例需要本机部署匹配的共享库，外部 HLS/RTSP 地址通过环境变量配置。

CI 为手动触发，测试需显式启用。
bin/obj/TestResults 不提交。原生库部署见 [FFmpeg 说明](../docs/ffmpeg-deployment.md)。
本机原生库目录按当前 RID 使用 `FFmpeg/<RID>/native`；可通过 `FLUXMEDIA_FFMPEG_ROOT` 覆盖，目录需包含完整的共享库集合。
