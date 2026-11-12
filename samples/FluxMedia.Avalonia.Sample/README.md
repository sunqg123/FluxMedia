# 项目引用示例

通过 ProjectReference 使用当前两个库，演示 `ObservableCollection<Player>`、基础操作控件和自定义 UI。

## 目录结构

| 目录 | 职责 |
| --- | --- |
| `Models` | `PlayerInput` 保存 Demo 的媒体与录制路径 |
| `Views` | 文件选择、卡片布局、自定义控制栏与宿主全屏 |
| `ViewModels` | 主模型直接拥有 Player 集合，条目模型仅管理 Demo 辅助面板 |
| `Commands` | Demo 的输入、录制等命令 |
| `Infrastructure` | 属性变更通知基类 |

`App` 负责引擎初始化，`MainWindowViewModel.Players` 的类型为 `ObservableCollection<Player>`。
`ItemsControl` 的模板数据就是 Player，`FluxPlayerControl.Player` 直接绑定集合项。
`PlayerItemViewModel` 只服务于文件选择、录制和诊断，不再创建播放器，也不处理基础播放命令。
实际应用只需绑定控件，无须使用这个 Demo 辅助模型。

## 播放界面

Sample 在自己的 StandardControlBar 模板中定义播放按钮、进度和音量等操作，绑定类库命令。
操作区浮现在视频底部，鼠标移动、画面点击或触摸可显示；播放中闲置后自动隐藏。
拖动、悬停操作栏、键盘操作、展开菜单或发生错误时保留显示。
键盘空闲后恢复自动隐藏，控制栏中的焦点不会永久阻止隐藏。
进度与倍速沿用核心权限，直播禁止 Seek 和倍速。

勾选“精简控制栏”可在两个示例模板之间切换，录制与诊断区域保持常显。
“自定义倍速档位”演示设置 0.1、0.25、0.6、1、1.3、1.8、2.5、4、8、10、12、16、20、25、30 倍，取消勾选后恢复播放器默认档位。
倍速列表由 Sample 设置到 `FluxPlayerControl.PlaybackSpeeds`，不修改核心默认值；音视频共同响应目标倍速。
核心默认范围为 0.1～10，自动推算四级音频滤镜；Sample 在 App.axaml.cs 调用 FluxEngine.Start 时将 FluxEngineOptions.MaxPlaybackRate 配置为 30，自动得到五级，无需另填预算。启动时统一校验，诊断显示当前上下限，Sample 不重复校验范围。
倍速选择显示已应用值，待应用标识显示在控制栏；诊断同时显示目标、实际值、请求编号、状态和错误。
暂停时选择速度属于 Accepted，恢复后才提交；暂停中 Seek 会作废未提交目标并刷新选择器。
按钮文字、布局及错误提示均由 Sample 定义；两个类库没有多语言处理或语言资源键。
全屏按钮通知 MainWindow，宿主将同一个播放器控件铺满窗口；再次点击或按 Escape 恢复列表。
不会为全屏另建第二个绑定同一 Player 的显示控件。

集合所有者负责移除和释放 Player。卡片卸载只停止自己的诊断计时器，不停止播放器。
详细用法见 [Avalonia 控件](../../docs/avalonia-controls.md)。

## 原生依赖

Sample 的 ResolveFFmpegRoot 先检查 FLUXMEDIA_FFMPEG_ROOT，再从程序目录向父目录查找
`FFmpeg/<RID>/native`，通过 FFmpegPath 传入核心，不向输出目录复制原生库。
克隆仓库后执行 `git lfs pull`，获取 Windows x64 原生库和 Sample.mp4。

应用先调用 FluxEngine.Start，成功后才创建播放器；退出时释放播放器并关闭引擎。
