using FluxMedia.Playback;

namespace FluxMedia.Tests;

/// <summary>
/// 播放边界测试。
/// </summary>
public sealed class PlaybackBoundaryTests
{
    /// <summary>
    /// 验证解决方案仅包含播放相关项目。
    /// </summary>
    [Fact]
    public void Solution_ContainsOnlyPlayerFirstProjects()
    {
        var solution = File.ReadAllText(Path.Combine(RepositoryRoot, "FluxMedia.slnx"));

        Assert.Contains("FluxMedia.Playback", solution, StringComparison.Ordinal);
        Assert.DoesNotContain("FluxMedia.Camera", solution, StringComparison.Ordinal);
        Assert.DoesNotContain("FluxMedia.ScreenRecording", solution, StringComparison.Ordinal);
        Assert.DoesNotContain("FluxMedia.Recording", solution, StringComparison.Ordinal);
        Assert.DoesNotContain("FluxMedia.Capture", solution, StringComparison.Ordinal);
        Assert.DoesNotContain("FluxMedia.Rendering", solution, StringComparison.Ordinal);
    }

    /// <summary>
    /// 验证源码目录包含播放库和 Avalonia 显示库。
    /// </summary>
    [Fact]
    public void SourceTree_ContainsPlaybackAndAvaloniaLibraries()
    {
        var projectFiles = Directory
            .GetFiles(Path.Combine(RepositoryRoot, "src"), "*.csproj", SearchOption.AllDirectories)
            .Select(Path.GetFileName)
            .OrderBy(static name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            new[] { "FluxMedia.Avalonia.csproj", "FluxMedia.Playback.csproj" }, projectFiles);
    }

    /// <summary>
    /// 验证播放项目不再引用已移除功能。
    /// </summary>
    [Fact]
    public void PlaybackProject_DoesNotReferenceRemovedFeatureProjects()
    {
        var project = File.ReadAllText(Path.Combine(RepositoryRoot, "src", "FluxMedia.Playback", "FluxMedia.Playback.csproj"));

        Assert.DoesNotContain("FluxMedia.Camera", project, StringComparison.Ordinal);
        Assert.DoesNotContain("FluxMedia.ScreenRecording", project, StringComparison.Ordinal);
        Assert.DoesNotContain("FluxMedia.Recording", project, StringComparison.Ordinal);
        Assert.DoesNotContain("FluxMedia.Capture", project, StringComparison.Ordinal);
        Assert.DoesNotContain("FluxMedia.Rendering", project, StringComparison.Ordinal);
    }

    /// <summary>
    /// 仅保留引擎创建入口，不再导出独立 Runtime 和兼容工厂。
    /// </summary>
    [Fact]
    public async Task Engine_IsTheOnlyPublicCreationEntryPoint()
    {
        await using var player = FluxEngine.CreatePlayer();
        Assert.Equal(PlaybackState.Created, player.State);
        var assembly = typeof(Player).Assembly;
        Assert.Null(assembly.GetType("FluxMedia.Playback.FluxPlayback"));
        Assert.Null(assembly.GetType("FluxMedia.Runtime.FluxMediaRuntime"));
        Assert.Empty(typeof(Player).GetConstructors());
        Assert.DoesNotContain(assembly.GetExportedTypes(), type => type.Name is "PlaybackSession" or "IPlaybackSession");
    }

    /// <summary>
    /// 验证多个播放器的实例和会话相互隔离。
    /// </summary>
    [Fact]
    public async Task PlaybackFacade_MultiplePlayersAreIsolated()
    {
        await using var first = FluxEngine.CreatePlayer();
        await using var second = FluxEngine.CreatePlayer();

        Assert.NotEqual(first.InstanceId, second.InstanceId);
        Assert.Equal(PlaybackState.Created, first.State);
        Assert.Equal(PlaybackState.Created, second.State);
    }

    /// <summary>
    /// 项目根目录。
    /// </summary>
    private static string RepositoryRoot =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
}
