using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using FluxMedia.Playback;

namespace FluxMedia.Avalonia.Sample;

/// <summary>
/// 配置应用主题并创建主窗口。
/// </summary>
public sealed partial class App : Application
{
    /// <summary>
    /// 加载应用界面和主题资源。
    /// </summary>
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    /// <summary>
    /// 应用框架就绪后创建主窗口。
    /// </summary>
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var ffmpegRoot = ResolveFFmpegRoot();
            FluxEngine.Start(new FluxEngineOptions
            {
                FFmpegPath = ffmpegRoot,
                EnableLogging = true,
                LogDirectory = Path.Combine(AppContext.BaseDirectory, "Logs")
            });
            desktop.Exit += (_, _) => FluxEngine.Shutdown();
            desktop.MainWindow = new MainWindow();
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// 查找可用的 FFmpeg 原生库目录。
    /// </summary>
    private static string? ResolveFFmpegRoot()
    {
        var environment = Environment.GetEnvironmentVariable("FLUXMEDIA_FFMPEG_ROOT");
        if (FFmpegLibraries.IsAvailable(environment))
        {
            return environment;
        }

        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "FFmpeg", "win-x64", "native");
            if (FFmpegLibraries.IsAvailable(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        return null;
    }
}
