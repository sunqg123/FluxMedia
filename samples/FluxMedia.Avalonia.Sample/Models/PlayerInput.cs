using FluxMedia.Avalonia.Sample.Infrastructure;

namespace FluxMedia.Avalonia.Sample.Models;

/// <summary>
/// 保存单个播放器的输入数据。
/// </summary>
public sealed class PlayerInput : NotifyObject
{
    /// <summary>
    /// 媒体路径或网络地址。
    /// </summary>
    private string _source = string.Empty;

    /// <summary>
    /// 录制输出路径。
    /// </summary>
    private string _recordingPath = string.Empty;

    /// <summary>
    /// 待打开的媒体地址。
    /// </summary>
    public string Source
    {
        get => _source;
        set => SetField(ref _source, value);
    }

    /// <summary>
    /// 录制文件的输出路径。
    /// </summary>
    public string RecordingPath
    {
        get => _recordingPath;
        set => SetField(ref _recordingPath, value);
    }
}
