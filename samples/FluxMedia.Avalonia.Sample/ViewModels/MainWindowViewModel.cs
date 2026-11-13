using FluxMedia.Avalonia.Sample.Commands;
using FluxMedia.Avalonia.Sample.Infrastructure;
using FluxMedia.Playback;
using System.Collections.ObjectModel;
using System.Windows.Input;

namespace FluxMedia.Avalonia.Sample.ViewModels;

/// <summary>
/// 管理播放器集合及其创建、释放。
/// </summary>
public sealed class MainWindowViewModel : NotifyObject, IAsyncDisposable
{
    /// <summary>
    /// 订阅集合变化并创建两个播放器。
    /// </summary>
    public MainWindowViewModel()
    {
        Players.CollectionChanged += (_, _) => OnPropertyChanged(nameof(PlayerCountText));
        AddPlayerCommand = new RelayCommand(AddPlayer);
        AddPlayer();
        AddPlayer();
    }

    /// <summary>
    /// 页面绑定的播放器集合。
    /// </summary>
    public ObservableCollection<Player> Players { get; } = new();

    /// <summary>
    /// 添加尚未打开媒体的播放器。
    /// </summary>
    public ICommand AddPlayerCommand
    {
        get;
    }

    /// <summary>
    /// 当前播放器数量。
    /// </summary>
    public string PlayerCountText => $"{Players.Count} 个播放器";

    /// <summary>
    /// 创建播放器并加入集合。
    /// </summary>
    private void AddPlayer() => Players.Add(FluxEngine.CreatePlayer());

    /// <summary>
    /// 从集合移除播放器并释放资源。
    /// </summary>
    public async Task RemovePlayerAsync(Player player)
    {
        if (!Players.Remove(player))
        {
            return;
        }
        await player.DisposeAsync();
    }

    /// <summary>
    /// 释放全部播放器并汇总清理异常。
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        var players = Players.ToArray();
        Players.Clear();
        var errors = new List<Exception>();
        foreach (var player in players)
        {
            try
            {
                await player.DisposeAsync();
            }
            catch (Exception exception)
            {
                errors.Add(exception);
            }
        }
        if (errors.Count > 0)
        {
            throw new AggregateException("示例播放器释放失败。", errors);
        }
    }
}
