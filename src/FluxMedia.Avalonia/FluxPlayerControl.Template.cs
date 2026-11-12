using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Layout;

namespace FluxMedia.Avalonia;

public sealed partial class FluxPlayerControl
{
    /// <summary>
    /// 默认模板的根布局。
    /// </summary>
    private Grid? _templateRoot;

    /// <summary>
    /// 创建画面和内容容器，避免跨版本加载预编译 XAML。
    /// </summary>
    private static Control CreateDefaultTemplate(FluxPlayerControl owner, INameScope scope)
    {
        var root = new Grid
        {
            Name = "PART_Root",
            Background = owner.Background,
            ClipToBounds = true
        };
        var video = new FluxPlayerView
        {
            Name = "PART_VideoView",
            Player = owner.Player,
            Stretch = owner.Stretch
        };
        var overlay = new ContentPresenter
        {
            Name = "PART_OverlayHost",
            Content = owner.OverlayContent,
            ContentTemplate = owner.OverlayContentTemplate,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch
        };
        var controls = new ContentPresenter
        {
            Name = "PART_ControlsHost",
            Content = owner.ControlBarTemplate is null ? null : owner.Presentation,
            ContentTemplate = owner.ControlBarTemplate,
            IsVisible = owner.AreControlsVisible,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch
        };

        scope.Register(root.Name, root);
        scope.Register(video.Name, video);
        scope.Register(overlay.Name, overlay);
        scope.Register(controls.Name, controls);
        root.Children.Add(video);
        root.Children.Add(overlay);
        root.Children.Add(controls);
        return root;
    }
}
