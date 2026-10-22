namespace LargerInventory.Backend;

/// <summary>
/// 储存实例的运行参数。实例化时注入，不使用静态配置。
/// </summary>
public sealed class StoreConfig
{
    /// <summary>内存中最多持有的页数（LRU 上限，不是容量上限）。</summary>
    public int MaxLoadedPages { get; set; } = 16;

    /// <summary>页空闲多久后允许被 LRU 淘汰（毫秒）。</summary>
    public long PageIdleUnloadMs { get; set; } = 60_000;

    /// <summary>
    /// 是否允许真正卸载页。持久化为空实现时必须为 false，否则会丢失 Item 数据。
    /// </summary>
    public bool EnablePageEviction { get; set; } = false;

    /// <summary>一次划批最多包含的指令数（控制分析成本与并行粒度，不是背压）。</summary>
    public int MaxBatchCommands { get; set; } = 256;

    /// <summary>是否维护全局类型聚合（关闭时退化为扫描页摘要）。</summary>
    public bool EnableGlobalAggregate { get; set; } = true;

    /// <summary>触发并行的无冲突分量数下限（当前执行器以串行为主，阈值预留）。</summary>
    public int ParallelMinComponents { get; set; } = 8;

    /// <summary>触发并行的估算总耗时下限（微秒，阈值预留）。</summary>
    public int ParallelMinCostUs { get; set; } = 1_000;

    /// <summary>并行度上限（阈值预留）。</summary>
    public int MaxParallelism { get; set; } = 4;
}
