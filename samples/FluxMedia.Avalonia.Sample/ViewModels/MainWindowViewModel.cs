using FluxMedia.Avalonia.Sample.Commands;
using FluxMedia.Avalonia.Sample.Infrastructure;
using System.Collections.ObjectModel;
using System.Windows.Input;

namespace FluxMedia.Avalonia.Sample.ViewModels;

/// <summary>
/// 管理示例窗口中的播放器和界面命令。
/// </summary>
public sealed class MainWindowViewModel : NotifyObject, IAsyncDisposable
{
    /// <summary>
    /// 打开本地文件选择窗口的回调。
    /// </summary>
    private readonly Func<Task<string?>> _browse;

    /// <summary>
    /// 分配给下一个播放器的递增标识。
    /// </summary>
    private int _nextPlayerId = 1;

    /// <summary>
    /// 初始化主入口窗口视图模型所需的状态和资源。
    /// </summary>
    public MainWindowViewModel(Func<Task<string?>> browse)
    {
        _browse = browse;
        Players.CollectionChanged += (_, _) => OnPropertyChanged(nameof(PlayerCountText));
        AddPlayerCommand = new RelayCommand(AddPlayer);
        AddPlayer();
        AddPlayer();
    }

    /// <summary>
    /// 当前窗口管理的播放器集合。
    /// </summary>
    public ObservableCollection<PlayerItemViewModel> Players { get; } = new();

    /// <summary>
    /// 添加播放器命令。
    /// </summary>
    public ICommand AddPlayerCommand { get; }

    /// <summary>
    /// 播放器数量文本。
    /// </summary>
    public string PlayerCountText => $"{Players.Count} player(s)";

    /// <summary>
    /// 释放当前对象持有的资源。
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        foreach (var player in Players.ToArray())
        {
            await player.DisposeAsync();
        }

        Players.Clear();
    }

    /// <summary>
    /// 添加一个播放器列表项。
    /// </summary>
    private void AddPlayer()
    {
        var item = new PlayerItemViewModel(_nextPlayerId++, _browse, RemoveAsync);
        Players.Add(item);
        UpdateRemoveState();
    }

    /// <summary>
    /// 释放并移除指定播放器。
    /// </summary>
    private async Task RemoveAsync(PlayerItemViewModel item)
    {
        if (!Players.Remove(item)) return;
        await item.DisposeAsync();
        UpdateRemoveState();
    }

    /// <summary>
    /// 根据播放器数量更新移除操作的可用性。
    /// </summary>
    private void UpdateRemoveState()
    {
        foreach (var player in Players)
        {
            player.CanRemove = Players.Count > 1;
        }
    }
}
