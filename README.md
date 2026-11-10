# FluxMedia

FluxMedia 是开发中的 C# 播放核心，提供本地文件、网络播放、音频输出、Seek、录制和可选 Avalonia 显示控件。
当前维护仓库为 `D:\GithubServer\FluxMedia`。

最新源码仍在调整，尚未对本次 Seek 修正和仓库整理执行构建、回归或性能测量。
当前进展与未完成内容见 [项目状态](docs/current-state.md)。

## 项目结构

| 目录 | 当前用途 |
| --- | --- |
| `src/FluxMedia.Playback` | 播放核心、静态日志及原生调用 |
| `src/FluxMedia.Avalonia` | 可选的 Avalonia 显示控件 |
| `src/Shared` | 两个库共用的编译器标记源码 |
| `samples/FluxMedia.Avalonia.Sample` | 通过项目引用使用播放器的示例 |
| `demos/FluxMedia.DllDemo` | 通过 DLL 引用使用 SDK 的示例 |
| `tests` | 当前单元、回归及 UI 消费项目源码，按需手动执行 |
| `licenses` | 打包引用的第三方许可证 |
| `FFmpeg` | 本机原生库，仓库只保留部署说明 |
| `docs` | 当前 API、架构、部署和诊断说明 |
| `artifacts` | 本机输出和诊断材料，不提交到 Git |

## 基本使用

```csharp
using FluxMedia.Playback;

FluxEngine.Start(new FluxEngineOptions
{
    FFmpegPath = @"D:\Tools\ffmpeg\bin",
    ThrowOnMissingFFmpeg = true,
    EnableLogging = true
});

await using var player = FluxEngine.CreatePlayer();
await player.OpenAndPlayAsync(@"D:\media\video.mp4");
if (player.CanSeek)
    await player.SeekAsync(TimeSpan.FromSeconds(30));
await player.StopAsync();
```

创建播放器统一使用 `FluxEngine.CreatePlayer`。宿主退出时调用 `FluxEngine.ShutdownAsync`。
自定义 UI 使用 `Player.CanSeek` 显示交互能力；直播的 Seek 在核心层同样被拒绝。

## 依赖与本机材料

- 当前默认绑定为 `FFmpeg.AutoGen 9.0.1.1`，统一配置在 `Directory.Build.props`。
- `FFmpegLibraries` 集中生成匹配库名和校验 ABI；任意 FFmpeg 主版本自动适配仍未实现。
- 原生库由应用自行部署，通过路径或 `FLUXMEDIA_FFMPEG_ROOT` 指定；本机示例默认查找 `FFmpeg/win-x64/native`。
- 根目录 `Sample.mp4` 是可选的本机调试素材，不提交到 Git，克隆后可自行选择媒体。
- 日志和性能数据保存在本机，不作为源码或当前验证通过的证明。

## 文档

- [当前状态与维护规则](docs/current-state.md)
- [API 使用](docs/api.md)
- [架构](docs/architecture.md)
- [FFmpeg 部署与版本边界](docs/ffmpeg-deployment.md)
- [日志](docs/logging.md)
- [Seek 交互与权限](docs/seek-preview.md)
- [用户 Seek 日志分析](docs/seek-log-analysis.md)
- [原生库目录](FFmpeg/README.md) · [测试目录](tests/README.md)

CI 仅手动触发；测试还需显式选择。仓库整理没有执行构建、测试、打包或推送。
