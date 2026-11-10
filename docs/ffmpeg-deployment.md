# FFmpeg 原生库部署与版本切换

当前默认托管绑定为 FFmpeg.AutoGen 9.0.1.1。NuGet 包不分发 FFmpeg 原生库。
应用使用 `FluxEngineOptions.FFmpegPath` 或 `FLUXMEDIA_FFMPEG_ROOT` 指定共享库目录。
本轮源码修改未构建、未测试；旧测量结果不能代表当前版本。

## 统一维护位置

- `src/FluxMedia.Playback/FFmpegLibraries.cs`：ABI 主版本、平台命名规则、目录检查和实际版本校验。
- `Directory.Build.props` 的 `FFmpegAutoGenVersion`：所有项目共享的绑定包版本。

库的 ABI 主版本直接来自所选 AutoGen 包的 `LIB*_VERSION_MAJOR` 常量，
不再在代码中维护 avcodec-62/63 等数字列表。升级对应绑定后，文件名自动随 ABI 更新。

```csharp
using FluxMedia.Playback;

var binding = FFmpegLibraries.BindingVersion;
var names = FFmpegLibraries.RequiredFileNames;
var complete = FFmpegLibraries.IsAvailable(nativeDirectory);
```

`IsAvailable` 只检查与当前绑定匹配的完整文件集合，不加载原生代码，
不检查架构、导出函数或文件内容；初始化时再加载并核对实际 ABI。
BindingVersion 返回实际引用的绑定程序集版本；初始化日志同时记录绑定版本、原生版本和目录。
因此示例可以在引擎启动前调用，缺少一个依赖的目录不会被选中。

## 为什么仍要文件名

AutoGen 本身根据逻辑库名、ABI 主版本与平台规则构造动态库文件名。
本项目按相同约定生成名称，不通过重命名 DLL 或扫描后选择任意最高版本来绕过 ABI。
仅仅发现一个 DLL 文件，不能证明其中的结构布局与当前 C# 绑定匹配。

Windows 使用 `{name}-{major}.dll`，Linux 使用 `lib{name}.so.{major}`，
macOS 使用 `lib{name}.{major}.dylib`。Unix 部署需要保留对应 ABI 名称的常规符号链接。
原生库加载统一来自选中的同一个目录，避免从多个目录拼出依赖集合。

依据：[AutoGen 的原生函数解析器](https://github.com/Ruslan-B/FFmpeg.AutoGen/blob/master/FFmpeg.AutoGen/FunctionResolverBase.cs)
及 [生成的 ABI 版本表](https://github.com/Ruslan-B/FFmpeg.AutoGen/blob/master/FFmpeg.AutoGen/generated/ffmpeg.libraries.g.cs)。

## 当前版本适配边界

目标是按实际 API 能力支持不同 FFmpeg 版本；当前尚未实现任意主版本适配。
当前源码直接访问 AutoGen 结构体，不能只替换 DLL 或修改绑定包版本就认定兼容。
后续升级需同时检查结构布局、导出函数与调用约定，再由用户安排统一验证。

`FFmpegAutoGenVersion` 仅集中配置托管依赖版本，不代表所有绑定版本均可直接编译。
测试源码的素材工具目录统一读取 FFmpegFactAttribute.NativeRoot，优先使用 FLUXMEDIA_FFMPEG_ROOT。
切换版本、原生目录或原生构建后应启动新进程；引擎 Shutdown 不卸载进程级原生函数绑定，不能用它热切换 ABI。

不同 ABI 的库不能改名后直接使用。
同一 ABI 主版本下的常规补丁升级通常保持文件名，无需修改源码中的库名。

## 平台与分发

使用同一构建的库和命令行工具；测试所需工具名称也由 FFmpegLibraries 提供。
生产播放不要求 ffmpeg/ffprobe 命令行工具存在。
Linux 音频还需要 OpenAL；macOS 使用系统 OpenAL Framework，相关平台仍需实机验证。

原生库的 LGPL/GPL 与编解码配置由应用分发方选择，
见 `THIRD-PARTY-NOTICES.txt` 和 `licenses/`。

当前实现与未验证项见 [项目状态](current-state.md)。
