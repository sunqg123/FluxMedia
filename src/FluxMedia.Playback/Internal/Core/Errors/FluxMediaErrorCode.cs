namespace FluxMedia.Core.Errors;

/// <summary>
/// FluxMedia 统一错误码。
/// </summary>
public enum FluxMediaErrorCode
{
    /// <summary>
    /// 未知。
    /// </summary>
    Unknown = 0,
    /// <summary>
    /// 媒体打开失败。
    /// </summary>
    MediaOpenFailed,
    /// <summary>
    /// 媒体解码失败。
    /// </summary>
    MediaDecodeFailed,
    /// <summary>
    /// 音频输出失败。
    /// </summary>
    AudioOutputFailed,
    /// <summary>
    /// 视频渲染失败。
    /// </summary>
    VideoRenderFailed,
    /// <summary>
    /// 录制失败。
    /// </summary>
    RecordingFailed = 9,
    /// <summary>
    /// 原生库加载失败。
    /// </summary>
    NativeLibraryLoadFailed = 11,
    /// <summary>
    /// 平台不可支持。
    /// </summary>
    PlatformNotSupported,
    /// <summary>
    /// 权限被拒绝。
    /// </summary>
    PermissionDenied,
    /// <summary>
    /// 设备忙碌。
    /// </summary>
    DeviceBusy,
    /// <summary>
    /// 网络错误。
    /// </summary>
    NetworkError,
    /// <summary>
    /// 资源已释放。
    /// </summary>
    ResourceDisposed,
    /// <summary>
    /// 操作已取消。
    /// </summary>
    OperationCanceled,
    /// <summary>
    /// 无效状态。
    /// </summary>
    InvalidState
}
