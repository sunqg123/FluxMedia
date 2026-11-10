namespace FluxMedia.Core.Errors;

/// <summary>
/// 统一错误模型。用于异常、事件通知和 UI 提示。
/// </summary>
/// <param name="Code">结构化错误码。</param>
/// <param name="Message">当前事件或操作的说明。</param>
/// <param name="Module">生成日志或错误的模块名称。</param>
/// <param name="InstanceId">当前组件的实例标识。</param>
/// <param name="CurrentState">发生错误时的组件状态。</param>
/// <param name="MediaSource">发生错误时使用的媒体地址。</param>
/// <param name="DeviceId">相关设备的唯一标识。</param>
/// <param name="DeviceName">相关设备的显示名称。</param>
/// <param name="Platform">发生错误时的运行平台。</param>
/// <param name="FFmpegErrorCode">FFmpeg 返回的原生错误码。</param>
/// <param name="SuggestedAction">建议调用方采取的处理方式。</param>
public sealed record FluxMediaError(
    FluxMediaErrorCode Code, string Message, string Module, Guid InstanceId, string? CurrentState = null, string? MediaSource = null, string? DeviceId = null, string? DeviceName = null, string? Platform = null, int? FFmpegErrorCode = null, string? SuggestedAction = null);
