# Avalonia 播放器控件

`FluxPlayerView` 只显示画面。`FluxPlayerControl` 提供画面、基础操作命令、控制栏显隐和业务内容容器。
两个控件都绑定外部 Player，由应用负责创建和释放。

类库不提供固定按钮布局、显示文字、语言包、翻译接口或语言资源键。
按钮、进度条、提示和错误展示均由使用方页面或模板定义，语言处理也由使用方决定。
类库通过 C# 构建默认容器模板，只包含视频层和内容容器，不需要应用额外引用 StyleInclude。
使用方的界面 XAML 由使用方项目编译，避免直接跨 Avalonia 大版本加载类库预编译模板。

## 绑定播放器集合

```csharp
public ObservableCollection<Player> Players { get; } = new();

private async Task AddAsync(string source)
{
    var player = FluxEngine.CreatePlayer();
    Players.Add(player);
    await player.OpenAndPlayAsync(source);
}
```

应用先完成 FluxEngine.Start。动态列表使用 ObservableCollection，固定列表也可以使用 List。
添加集合项不会自动播放；移除后仍需由应用调用 Player.DisposeAsync。
集合增删、模板和控件属性修改应在 UI 线程执行。

下面的模板属于使用方，按钮文字可换成业务项目自己的内容：

```xml
<ItemsControl xmlns="https://github.com/avaloniaui"
              xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
              xmlns:flux="clr-namespace:FluxMedia.Avalonia;assembly=FluxMedia.Avalonia"
              xmlns:playback="clr-namespace:FluxMedia.Playback;assembly=FluxMedia.Playback"
              xmlns:presentation="clr-namespace:FluxMedia.Avalonia.Bindings;assembly=FluxMedia.Avalonia"
              ItemsSource="{Binding Players}">
    <ItemsControl.ItemTemplate>
        <DataTemplate x:DataType="playback:Player">
            <flux:FluxPlayerControl Player="{Binding}" Height="360">
                <flux:FluxPlayerControl.ControlBarTemplate>
                    <DataTemplate x:DataType="presentation:PlayerBinding">
                        <Border VerticalAlignment="Bottom" Padding="10" Background="#CC182030">
                            <StackPanel Spacing="8">
                                <Slider Minimum="0" Maximum="{Binding DurationSeconds}"
                                        Value="{Binding PositionSeconds, Mode=TwoWay}"
                                        IsEnabled="{Binding CanSeek}" />
                                <StackPanel Orientation="Horizontal" Spacing="8">
                                    <Button Content="Play" IsVisible="{Binding !IsPlaying}"
                                            Command="{Binding $parent[flux:FluxPlayerControl].PlayPauseCommand}" />
                                    <Button Content="Pause" IsVisible="{Binding IsPlaying}"
                                            Command="{Binding $parent[flux:FluxPlayerControl].PlayPauseCommand}" />
                                    <Button Content="Stop"
                                            Command="{Binding $parent[flux:FluxPlayerControl].StopCommand}" />
                                    <TextBlock Text="{Binding TimeText}" Foreground="White" />
                                </StackPanel>
                            </StackPanel>
                        </Border>
                    </DataTemplate>
                </flux:FluxPlayerControl.ControlBarTemplate>
            </flux:FluxPlayerControl>
        </DataTemplate>
    </ItemsControl.ItemTemplate>
</ItemsControl>
```

## 基础操作

ControlBarTemplate 的数据是 `FluxMedia.Avalonia.Bindings.PlayerBinding`，进度、音量、静音、倍速和能力绑定它的属性。
控件创建并管理该适配器，外部仍直接绑定 Player，无须另外维护集合或视图模型。
`FluxPlayerControl.Presentation` 暴露当前绑定对象，模板外可绑定 `#PlaybackControl.Presentation.State` 等属性。
绑定对象保存拖动预览；真实位置从其 Player.Position 或核心进度事件读取。
进度数值按实际变化通知，保留核心原有更新节奏；总时长只在变化时通知，时间文本按显示秒数变化生成并缓存。
普通控制刷新按播放状态、倍速和音量分组比较，仅通知发生变化的组；录制诊断沿用控制刷新通知，由订阅方按需读取。
控制请求失败时仍会回读实际值，不能因值未改变而省略恢复界面的通知。
PlayerBinding.Recording 在媒体切换中可能为空；控制权限和录制状态继续使用 CanSeek、CanChangeSpeed、RecordingState 等业务属性。
命令通过父级 FluxPlayerControl 获取，无须使用方包装播放器集合项。
不提供控制栏模板时，只显示画面和使用方覆盖内容，不生成按钮或播放器文本。

| 命令 | 用途 |
| --- | --- |
| PlayPauseCommand | 播放或暂停 |
| StopCommand | 停止播放 |
| SeekBackwardCommand | 后退十秒 |
| SeekForwardCommand | 快进十秒 |
| FullScreenCommand | 请求宿主处理全屏，无订阅者时禁用 |

`IsBusy` 表示控件命令正在执行。直播的 Seek 和倍速权限仍由核心执行。
控件接收核心故障并通过 ErrorText 保留原始诊断说明，不自行弹窗或显示错误文案。
应用可以从操作结果、Player.Faulted 的错误信息决定如何展示错误，也可自行映射错误码。

## 自定义倍速档位

`PlaybackSpeeds` 可直接赋值或绑定使用方列表。未设置或绑定值为 null 时采用
`FluxPlayerControl.DefaultSpeeds`：0.5、0.75、1、1.25、1.5、2、3、4。
列表只是界面候选项，双向赋值使用 `PlayerBinding.Speed`，业务调用使用 `Player.SetSpeedAsync`。
实际允许范围由 FluxEngineOptions 在启动时统一配置，默认 0.1～10，音频级数自动推算为四级；列表不绕过范围和直播权限。
控件和 Sample 不另设倍速边界，越界由核心返回失败，界面回读实际速度并显示操作错误。

```csharp
// 启动时已配置 MaxPlaybackRate = 30，音频级数自动推算为五级。
playbackControl.PlaybackSpeeds = new[] { 0.1, 0.25, 0.6, 1.0, 1.3, 2.5, 4.0, 8.0, 10.0, 12.0, 16.0, 20.0 };
// 清除使用方设置，恢复默认档位。
playbackControl.ClearValue(FluxPlayerControl.PlaybackSpeedsProperty);
```

控制栏模板中的选择器可绑定控件提供的列表：

```xml
<ComboBox ItemsSource="{Binding $parent[flux:FluxPlayerControl].PlaybackSpeeds}"
          SelectedItem="{Binding Speed, Mode=TwoWay}"
          IsEnabled="{Binding CanChangeSpeed}" />
```

用户档位中的非法值仍由核心拒绝，直播不能绕过倍速限制。
绑定赋值先检查 CanChangeSpeed，不允许变速时回读实际值，不提交命令；直接调用 Player.SetSpeedAsync 仍由核心完整校验并返回失败结果。
绑定对象订阅核心 PlaybackChanged 并合并 UI 派发，`IsSpeedChangePending` 可用于显示等待状态；倍速选择器绑定 PlayerBinding。
`PlayerBinding.Speed` 读取核心已应用速度，`RequestedSpeed` 是目标；Accepted、应用成功、失败及 Seek 作废都会刷新绑定。
操作错误通过 `PlayerBinding.LastSpeedError` 展示，界面语言和文案仍由使用方决定。

## 控制栏显隐

鼠标移动或画面点击显示控制栏，播放中闲置三秒后自动隐藏。
拖动、控制栏悬停、键盘操作或菜单展开期间保持显示。
鼠标移动和按键只更新最后活动时间，不会逐次重启计时器。
键盘停止操作后按 AutoHideDelay 自动隐藏，单纯保留焦点不会持续阻止隐藏；隐藏时焦点返回播放器。
触摸使用点击切换；画面获得焦点后，空格切换播放/暂停。

AutoHideControls=False 或 AutoHideDelay 为零时关闭自动隐藏。
ShowControls、HideControls 可由页面主动调用，AreControlsVisible 可供使用方内容绑定。
模板可调整对齐方式、边距和布局；清空 ControlBarTemplate 后不再显示控制栏内容。

## 业务覆盖内容

OverlayContent 显示在画面上方，不随控制栏隐藏。
直接传入控件时继承使用方页面的数据上下文；OverlayContentTemplate 的数据由 OverlayContent 提供。
需要随控制栏隐藏的业务按钮放入 ControlBarTemplate，装饰内容可关闭命中测试。

```xml
<flux:FluxPlayerControl Player="{Binding CurrentPlayer}">
    <flux:FluxPlayerControl.OverlayContent>
        <Button Content="Snapshot" Command="{Binding CaptureCommand}"
                HorizontalAlignment="Right" VerticalAlignment="Top" Margin="12" />
    </flux:FluxPlayerControl.OverlayContent>
</flux:FluxPlayerControl>
```

CaptureCommand 和文字由使用方提供，类库不实现截图或语言切换。

## 全屏与生命周期

FullScreenRequested 仅通知宿主，窗口状态和页面布局由使用方处理。
Sample 演示同一个控件铺满窗口并支持 Escape 退出，未创建第二份视频显示控件。
Sample 的文件选择、录制、诊断及按钮文字都属于示例，不是类库内置界面。

卸载控件只停止自身计时和订阅，视频显示层释放显示资源，Player 继续由应用管理。
卸载或替换 Player 时释放旧 PlayerBinding，排队通知与异步完成不再更新旧界面；已提交的核心操作继续由 Player 管理。
使用方自行 new PlayerBinding(player) 时，应在 UI 线程创建，并在结束使用时调用 Dispose，以解除 Player 的事件订阅；播放器仍由其所有者释放。
替换 Player 时同步更新显示、控制栏数据和状态订阅。
整体替换标准 Template 时，可沿用 PART_VideoView、PART_ControlsHost、PART_OverlayHost 三个部件名。
部件类型分别为 FluxPlayerView、ContentPresenter、ContentPresenter；可选的 PART_Root 使用 Grid 承接背景。
每次应用模板都会同步现有 Player、Stretch、背景、覆盖内容及其模板、控制栏模板和显示状态。
同步使用 SetCurrentValue 保留部件上的绑定；替换模板本身不会强制展开原本隐藏的控制栏。
通常只需提供 ControlBarTemplate 或 OverlayContent，无须重写显示逻辑。

## 绑定迁移

1.1.5 包含公开 API 与绑定契约变更，升级旧版本的调用方需要同步调整以下入口。

自定义 ControlBarTemplate 的 x:DataType 从 playback:Player 改为 presentation:PlayerBinding。
PositionSeconds、DurationSeconds、TimeText、Speed、Volume、IsMuted 等模板绑定名称保持不变。
Player 不再实现 INotifyPropertyChanged，不再提供秒数、时间文本、界面默认档位或属性赋值控制入口。
非 UI 调用方使用 SeekAsync、SetSpeedAsync、SetVolumeAsync、SetMutedAsync，读取核心只读状态并订阅事件。
