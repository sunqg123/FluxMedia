namespace FluxMedia.Core.Models;

/// <summary>
/// 管理视频帧底层资源的引用计数。
/// </summary>
internal interface IVideoFrameOwner
{
    /// <summary>
    /// 增加底层资源的持有引用。
    /// </summary>
    void AddReference();

    /// <summary>
    /// 释放一个资源引用，最后一个引用负责清理。
    /// </summary>
    void Release();
}
