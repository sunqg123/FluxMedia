using Avalonia;

namespace FluxMedia.Avalonia.Sample;

/// <summary>
/// 配置并启动 Avalonia 应用。
/// </summary>
internal static class Program
{
    /// <summary>
    /// 启动示例应用。
    /// </summary>
    [STAThread]
    public static void Main(string[] args)
    {
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    /// <summary>
    /// 配置 Avalonia 平台和应用入口。
    /// </summary>
    public static AppBuilder BuildAvaloniaApp()
    {
        return AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
    }
}
