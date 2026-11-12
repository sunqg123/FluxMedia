namespace FluxMedia.Core.Logging;

/// <summary>
/// 通过引用计数管理输出目标的配置所有权与在途调用。
/// </summary>
internal sealed class LogTarget
{
    /// <summary>
    /// 配置引用与正在使用目标的调用数量。
    /// </summary>
    private int _references = 1;

    /// <summary>
    /// 创建持有一个配置引用的目标。
    /// </summary>
    public LogTarget(IFluxMediaLogSink? sink) => Sink = sink;

    /// <summary>
    /// 实际输出目标，空值表示未配置。
    /// </summary>
    public IFluxMediaLogSink? Sink { get; }

    /// <summary>
    /// 仅在目标尚未退役时增加使用引用。
    /// </summary>
    public bool TryRetain()
    {
        var count = Volatile.Read(ref _references);
        while (count > 0)
        {
            var actual = Interlocked.CompareExchange(ref _references, count + 1, count);
            if (actual == count)
            {
                return true;
            }
            count = actual;
        }
        return false;
    }

    /// <summary>
    /// 最后一个调用完成后释放目标，清理异常不向外传播。
    /// </summary>
    public void Release()
    {
        if (Interlocked.Decrement(ref _references) != 0)
        {
            return;
        }
        try
        {
            if (Sink is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
        catch
        {
            // 清理日志资源不能中断播放器。
        }
    }
}
