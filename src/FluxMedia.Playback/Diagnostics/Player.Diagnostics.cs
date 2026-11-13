namespace FluxMedia.Playback;

public sealed partial class Player
{
    /// <summary>
    /// 当前录制诊断；媒体切换中可能为空。
    /// </summary>
    public PlaybackRecordingDiagnosticInfo? Recording => _session.GetRecordingDiagnosticInfo();

    /// <summary>
    /// 读取稳定诊断快照，切换中不可用时为空。
    /// </summary>
    public PlaybackDiagnosticInfo? GetDiagnosticInfo()
    {
        return _session.GetDiagnosticInfo();
    }

    /// <summary>
    /// 读取录制诊断，切换中不可用时为空。
    /// </summary>
    public PlaybackRecordingDiagnosticInfo? GetRecordingDiagnosticInfo()
    {
        return _session.GetRecordingDiagnosticInfo();
    }
}
