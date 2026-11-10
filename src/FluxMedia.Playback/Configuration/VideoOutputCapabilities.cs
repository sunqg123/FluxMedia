namespace FluxMedia.Playback;

/// <summary>
/// 显示端支持的 GPU 共享能力。
/// </summary>
public sealed record VideoOutputCapabilities
{
    /// <summary>
    /// 显示端能否导入 D3D11 共享纹理。
    /// </summary>
    public bool SupportsD3D11SharedTexture { get; init; }

    /// <summary>
    /// 显示端能否导入 Vulkan 文件描述符。
    /// </summary>
    public bool SupportsVulkanOpaqueFileDescriptor { get; init; }

    /// <summary>
    /// 显示设备的适配器标识。
    /// </summary>
    public byte[]? AdapterLuid { get; init; }

    /// <summary>
    /// 显示设备的唯一标识。
    /// </summary>
    public byte[]? DeviceUuid { get; init; }
}
