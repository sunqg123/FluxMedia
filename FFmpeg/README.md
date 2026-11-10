# FFmpeg 原生库

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

本目录中的 Windows x64 DLL/EXE 通过 Git LFS 随仓库提供。克隆或更新后执行 `git lfs pull` 获取完整文件。
CI 的 Windows 作业启用 LFS 文件获取；其他平台仍需部署对应依赖。
当前绑定与跨版本限制见 [部署说明](../docs/ffmpeg-deployment.md)，第三方许可保存在仓库根目录 `licenses`。
