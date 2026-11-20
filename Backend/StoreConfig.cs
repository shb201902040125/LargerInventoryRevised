using System;

namespace LargerInventory.Backend;

/// <summary>
/// 储存实例的运行参数。实例化时注入，不使用静态配置。
/// </summary>
public sealed class StoreConfig
{
    /// <summary>内存中最多持有的页数（LRU 上限，不是容量上限）。</summary>
    public int MaxLoadedPages { get; set; } = 16;

    /// <summary>页空闲多久后允许按 LRU 淘汰（毫秒）。</summary>
    public long PageIdleUnloadMs { get; set; } = 60_000;

    /// <summary>
    /// 是否允许真正卸载页。持久化为空实现时必须为 false，否则会丢失 Item 数据。
    /// 绑定目录的 Store 会在构造时自动置为 true。
    /// </summary>
    public bool EnablePageEviction { get; set; } = false;

    /// <summary>一次划批最多包含的指令数（控制单批执行量，不是背压）。</summary>
    public int MaxBatchCommands { get; set; } = 256;

    /// <summary>每次 Pump 最多连续处理的批数，防止回调不断投递指令时长时间占用主线程。</summary>
    public int MaxBatchesPerPump { get; set; } = 16;

    /// <summary>指令 Pump 的软耗时预算（毫秒）。已开始的批次及其回调会完整执行。</summary>
    public int MaxCommandPumpMs { get; set; } = 3;

    /// <summary>指令 Pump 的软页工作预算，按页级扫描、锁尝试和首次使用计量；批次之间检查。</summary>
    public int MaxCommandPageWorkPerPump { get; set; } = 64;

    /// <summary>队列中允许等待的最大指令数；超出时新指令立即以 Throttled 失败。</summary>
    public int MaxQueuedCommands { get; set; } = 4096;

    /// <summary>启动校准期间每次 Pump 最多处理的页数。</summary>
    public int MaxReconcilePagesPerPump { get; set; } = 4;

    /// <summary>是否维护全局类型聚合（关闭时退化为扫描页摘要）。</summary>
    public bool EnableGlobalAggregate { get; set; } = true;

    /// <summary>存档时等待执行锁的最长时间（毫秒）；超时则本次存档失败并报告。</summary>
    public int FlushLockTimeoutMs { get; set; } = 5000;

    /// <summary>将非法配置值钳制到可用范围。</summary>
    internal void Normalize()
    {
        MaxLoadedPages = Math.Max(1, MaxLoadedPages);
        PageIdleUnloadMs = Math.Max(0, PageIdleUnloadMs);
        MaxBatchCommands = Math.Clamp(MaxBatchCommands, 1, 4096);
        MaxBatchesPerPump = Math.Clamp(MaxBatchesPerPump, 1, 1024);
        MaxCommandPumpMs = Math.Clamp(MaxCommandPumpMs, 1, 60_000);
        MaxCommandPageWorkPerPump = Math.Clamp(MaxCommandPageWorkPerPump, 1, 1_000_000);
        MaxQueuedCommands = Math.Clamp(MaxQueuedCommands, 1, 1_000_000);
        MaxReconcilePagesPerPump = Math.Clamp(MaxReconcilePagesPerPump, 1, 1024);
        FlushLockTimeoutMs = Math.Clamp(FlushLockTimeoutMs, 0, 60_000);
    }
}
