# FluxMedia.Avalonia

类库目标框架为 netstandard2.1，提供：

- FluxPlayerView：视频显示。
- FluxPlayerControl：视频显示、基础操作命令、控制栏显隐和内容容器。

控件直接绑定 Player，列表可使用 ObservableCollection<Player>。
播放器由应用创建、打开和释放；控件挂载或卸载不负责停止或释放 Player。

ControlBarTemplate 提供操作区，OverlayContent 提供业务覆盖内容。
ControlBarTemplate 的数据为 FluxMedia.Avalonia.Bindings.PlayerBinding，由控件内部创建，通过 Presentation 属性提供给外部。
属性通知、UI 派发、时间显示和拖动预览由绑定对象处理；播放规则与真实状态继续由核心 Player 管理。
自行 new PlayerBinding(player) 时须在 UI 线程创建，并在页面结束使用时调用 Dispose 解除事件订阅；Dispose 不会释放 Player。
默认界面档位位于 FluxPlayerControl.DefaultSpeeds，核心不提供界面候选列表。
类库不内置按钮文案、语言包、翻译接口或语言资源键，具体内容和语言处理由使用方控制。
未提供控制栏模板时不会生成操作按钮。全屏由宿主响应 FullScreenRequested。

完整示例在 samples/FluxMedia.Avalonia.Sample，包含示例按钮、文件选择、录制和诊断。
接入说明见 [控件文档](https://github.com/sunqg123/FluxMedia/blob/main/docs/avalonia-controls.md)。
