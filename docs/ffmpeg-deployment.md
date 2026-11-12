# FFmpeg 原生库部署与版本切换

当前默认托管绑定为 FFmpeg.AutoGen 9.0.1.1。NuGet 包不分发 FFmpeg 原生库。
应用使用 `FluxEngineOptions.FFmpegPath` 或 `FLUXMEDIA_FFMPEG_ROOT` 指定共享库目录。
源码仓库通过 Git LFS 提供 Windows x64 原生库，执行 `git lfs pull` 后可在 `FFmpeg/win-x64/native` 找到；这不改变 NuGet 包的分发范围。

## 目录选择规则

1. 检查 `FluxEngineOptions.FFmpegPath` 显式指定的完整库目录。
2. 未指定、目录不存在或所需文件不完整时，检查 `FLUXMEDIA_FFMPEG_ROOT`。
3. 两者都不可用时 `FluxEngine.Start` 直接抛出异常，错误包含配置目录与所需文件名。

只保留这两个来源，不再支持附加目录、默认运行目录扫描或通配符找库。
目录内必须包含当前平台和绑定所需的完整库集合，不从多个目录拼接依赖。
开始加载后遇到错误直接抛出异常，不切换目录。当前进程加载过原生库后，更换目录需重启进程。

Sample 先检查环境变量，未找到完整库时从程序目录向父目录查找 `FFmpeg/<RID>/native`，将找到的目录显式传入核心。
这段仓库目录查找仅用于 Sample，原生库不复制到 bin 或发布目录。
脱离源码仓库运行时，可用 `FLUXMEDIA_FFMPEG_ROOT` 指定独立部署的库目录。核心不识别 Sample 的目录布局。
平台和架构选择与 FFmpeg 版本适配相互独立；只放入不同版本的文件不会自动获得跨 ABI 支持。

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

当前源码直接访问 AutoGen 结构体，原生库需要匹配绑定 ABI。
跨主版本升级需检查结构布局、导出函数与调用约定，不能只替换 DLL 或修改绑定包版本。

`FFmpegAutoGenVersion` 仅集中配置托管依赖版本，不代表所有绑定版本均可直接编译。
切换版本、原生目录或原生构建后应启动新进程；引擎 Shutdown 不卸载进程级原生函数绑定，不能用它热切换 ABI。

不同 ABI 的库不能改名后直接使用。
同一 ABI 主版本下的常规补丁升级通常保持文件名，无需修改源码中的库名。

## 平台与分发

Linux 音频还需要 OpenAL；macOS 使用系统 OpenAL Framework，相关平台仍需实机验证。

原生库的 LGPL/GPL 与编解码配置由应用分发方选择，
见 `THIRD-PARTY-NOTICES.txt` 和 `licenses/`。
