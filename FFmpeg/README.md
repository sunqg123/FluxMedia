# 本机 FFmpeg 原生库

Windows x64 示例和本机测试默认查找：

```text
FFmpeg/
  win-x64/
    native/
      与当前绑定 ABI 匹配的共享库
      ffmpeg.exe
      ffprobe.exe
```

共享库与命令行工具应来自同一个构建。播放核心本身不要求命令行工具；媒体测试需要这两个工具。
示例优先读取 `FLUXMEDIA_FFMPEG_ROOT`，未找到可用配置时向上查找上述目录。
其他平台或自定义目录通过该环境变量或 `FluxEngineOptions.FFmpegPath` 指定实际库目录。

本目录只提交这份说明，原生库及其他本机文件不上传 GitHub。
CI 不自动下载原生库；启用原生媒体测试前需要单独部署匹配依赖。
当前绑定与跨版本限制见 [部署说明](../docs/ffmpeg-deployment.md)，第三方许可保存在仓库根目录 `licenses`。
