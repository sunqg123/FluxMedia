using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace FluxMedia.Avalonia.Sample.Infrastructure;

/// <summary>
/// 提供属性变更通知。
/// </summary>
public abstract class NotifyObject : INotifyPropertyChanged
{
    /// <summary>
    /// 绑定属性值变化时发出的通知。
    /// </summary>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// 字段值变化时赋值并发送属性通知。
    /// </summary>
    protected bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    /// <summary>
    /// 触发指定属性的变更通知。
    /// </summary>
    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
