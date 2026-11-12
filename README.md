# FluxMedia

FluxMedia 是 C# 播放核心，提供本地文件、网络播放、音频输出、Seek、录制和可选 Avalonia 显示控件。

播放核心可用于自定义 UI。Avalonia 提供纯画面控件 `FluxPlayerView`，以及带基础操作、自动显隐和可自定义覆盖内容的 `FluxPlayerControl`。

## 主要功能

- 本地文件与网络媒体播放、暂停、恢复、音量控制。
- 点播定位与连续拖动预览，直播由核心限制 Seek。
- 点播支持 0.5～2 倍速，音频保持音调；直播不允许变速。
- 播放输出录制，采用压缩数据包重封装。
- 视频帧回调和异步消费，可接入自定义显示界面。
- 可选 Avalonia 播放器控件，提供基础命令和自定义控制栏、业务覆盖内容容器。
- 独立静态日志入口。

## 项目结构

| 目录 | 当前用途 |
| --- | --- |
| `src/FluxMedia.Playback` | 播放核心、静态日志及原生调用 |
| `src/FluxMedia.Avalonia` | 视频显示、基础操作与可自定义的 Avalonia 播放器控件 |
| `src/Shared` | 两个库共用的编译器标记源码 |
| `samples/FluxMedia.Avalonia.Sample` | 通过项目引用使用播放器的示例 |
| `licenses` | 打包引用的第三方许可证 |
| `FFmpeg` | Windows x64 原生库 |
| `docs` | 当前 API、架构、部署和诊断说明 |
| `Sample.mp4` | 示例视频素材 |

两个库均以 `netstandard2.1` 为目标框架。当前示例使用 .NET 10 和 Avalonia 12.0.4。

## 原生依赖

仓库通过 Git LFS 提供 Windows x64 FFmpeg 原生库和示例视频。安装 Git LFS 后克隆仓库：

```shell
git lfs install
git clone https://github.com/sunqg123/FluxMedia.git
cd FluxMedia
git lfs pull
```

已有本地仓库可在拉取更新后执行 `git lfs pull` 获取完整文件。
Sample 本地运行时，按当前平台和架构直接使用仓库中的固定位置：

```text
FFmpeg/<RID>/native/
```

核心优先检查 `FluxEngineOptions.FFmpegPath`；未指定、目录不存在或所需库不完整时，再检查 `FLUXMEDIA_FFMPEG_ROOT`。
核心不扫描其他默认目录。Sample 先读取环境变量，未找到完整库时从程序目录向父目录查找 `FFmpeg/<RID>/native`，将结果传入核心；不向 bin 或发布目录复制原生库。
当前默认绑定为 `FFmpeg.AutoGen 9.0.1.1`，版本集中配置在 `Directory.Build.props`。
原生库必须匹配绑定 ABI；任意 FFmpeg 主版本自动适配尚未实现。
其他平台需自行准备对应的共享库。详细目录结构见 [原生库部署说明](docs/ffmpeg-deployment.md)。

## 基本使用

```csharp
using FluxMedia.Playback;

FluxEngine.Start(new FluxEngineOptions
{
    FFmpegPath = Environment.GetEnvironmentVariable("FLUXMEDIA_FFMPEG_ROOT"),
    EnableLogging = true
});

await using var player = FluxEngine.CreatePlayer();
await player.OpenAndPlayAsync("video.mp4");
if (player.CanSeek)
    await player.SeekAsync(TimeSpan.FromSeconds(30));
await player.StopAsync();
```

示例中的媒体路径按实际文件替换；引擎初始化前需配置好原生库路径。
应用启动时先调用一次 `FluxEngine.Start`，成功后再用 `FluxEngine.CreatePlayer` 创建播放器。
初始化失败或未初始化就创建播放器都会抛出异常，不会自动启动或重试。
宿主退出时先释放播放器，再调用 `FluxEngine.ShutdownAsync`。
自定义 UI 使用 `Player.CanSeek` 显示交互能力；直播的 Seek 在核心层同样被拒绝。
Player 提供只读实际状态、异步命令和普通事件；Avalonia 的属性通知、时间显示和双向绑定由控件内部的 PlayerBinding 处理。

## 示例

`samples/FluxMedia.Avalonia.Sample` 通过项目引用演示播放器接入和多播放器界面，可用于播放功能调试。
获取 Git LFS 文件后，可使用仓库根目录的 `Sample.mp4`，也可自行选择媒体文件。

生成输出和日志仍由 Git 忽略规则排除；`Sample.mp4` 和 FFmpeg 目录中的 DLL/EXE 通过 Git LFS 管理。

## 文档

- [API 使用](docs/api.md)
- [Avalonia 控件与自定义界面](docs/avalonia-controls.md)
- [架构](docs/architecture.md)
- [FFmpeg 部署与版本边界](docs/ffmpeg-deployment.md)
- [日志](docs/logging.md)
- [Seek 交互与权限](docs/seek-preview.md)

## 许可证

项目许可证见 [LICENSE](LICENSE)，第三方说明见 [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt)。
第三方许可证文本保存在 `licenses`，FFmpeg 原生库需按实际构建的许可证和配置处理。
