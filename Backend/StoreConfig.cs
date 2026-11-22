using System;

namespace LargerInventory.Backend;

/// <summary>
/// 仓储及其集成服务的运行参数。存储实例可在构造时注入自己的配置。
/// </summary>
public sealed class StoreConfig
{
    // 槽位冲突调度器支持的并发上限。
    internal const int MaxSupportedConcurrentCommands = 8;
    internal const int MaxSupportedConcurrentSlots = 32;

    /// <summary>已加载页的淘汰目标。调高可减少重新载入并增加常驻内存。钉住或仍脏的页可以暂时超过此目标。</summary>
    public int MaxLoadedPages { get; set; } = 16;

    /// <summary>允许淘汰空闲页的时间阈值（毫秒）。调高可延长热页驻留，调低可更快回收内存。超过加载页数目标时可提前卸载。</summary>
    public long PageIdleUnloadMs { get; set; } = 60_000;

    /// <summary>
    /// 是否卸载可从磁盘重新加载的页。关闭可减少重复加载，同时增加常驻内存。
    /// 无持久化目录的仓库始终关闭淘汰。
    /// </summary>
    public bool EnablePageEviction { get; set; } = true;

    /// <summary>一次划批最多包含的指令数（控制单批执行量，不是背压）。调高可增加批内合并机会，并延长到达预算检查和回调的时间。</summary>
    public int MaxBatchCommands { get; set; } = 256;

    /// <summary>不可拆分的控制流区间或事务的最大指令数。调高会增加单次推进及事务日志的开销。</summary>
    public int MaxControlFlowCommands { get; set; } = 256;

    /// <summary>一次控制流执行最多访问的指令数，重复访问也计入。调高允许更长的循环，也可能增加帧耗时。</summary>
    public int MaxControlFlowSteps { get; set; } = 1024;

    /// <summary>同一批次内最多同时运行的独立指令数（上限 8）。设为 1 可关闭指令间并发。调高可容纳更多独立命令，同时增加线程池派发和等待成本。</summary>
    public int MaxConcurrentCommandsPerBatch { get; set; } = 4;

    /// <summary>参与并发波次的单条强指定指令允许声明的最大槽位数（上限 32）。Put 的输入堆叠数也受此限制。调高允许更多强指定命令参与波次，同时增加冲突分析、槽位捕获及碰撞开销。</summary>
    public int MaxConcurrentSlotsPerCommand { get; set; } = 16;

    /// <summary>一次主动清空指令队列时最多执行的批数，避免回调持续投递导致无法结束。调高可在关闭时执行更多积压命令，同时延长关闭时间。超出预算的命令明确失败。</summary>
    public int MaxDrainBatches { get; set; } = 1024;

    /// <summary>每次 Pump 最多连续处理的批数，防止回调不断投递指令时长时间占用主线程。调高可更快消化积压，并可能让一次推进更久。</summary>
    public int MaxBatchesPerPump { get; set; } = 16;

    /// <summary>指令 Pump 的软耗时预算（毫秒）。已开始的批次及其回调会完整执行。调低会更早在批次间让出执行权，可能增加排队等待。</summary>
    public int MaxCommandPumpMs { get; set; } = 3;

    /// <summary>指令 Pump 的软页工作预算，按页级扫描、锁尝试和首次使用计量；批次之间检查。调高可在一次推进中扫描更多页，同时可能增加帧尾耗时。</summary>
    public int MaxCommandPageWorkPerPump { get; set; } = 64;

    /// <summary>队列中允许等待的最大指令数；超出时新指令立即以 Throttled 失败。调高能吸收更大的突发请求，也会增加积压内存和等待时间。</summary>
    public int MaxQueuedCommands { get; set; } = 4096;

    /// <summary>启动校准期间每次 Pump 最多处理的页数。调高可缩短启动校准总时间，也可能增加单帧加载耗时。</summary>
    public int MaxReconcilePagesPerPump { get; set; } = 4;

    /// <summary>是否维护全局类型聚合（关闭时退化为扫描页摘要）。开启可加快类型查询并增加索引内存及更新工作。</summary>
    public bool EnableGlobalAggregate { get; set; } = true;

    /// <summary>存档时等待执行锁的最长时间（毫秒）；超时则本次存档失败并报告。调高可减少锁繁忙导致的保存失败，也会让调用方等待更久。</summary>
    public int FlushLockTimeoutMs { get; set; } = 5000;

    /// <summary>服务端周期存档的间隔（毫秒）。调高可减少周期存档频率，并延长更新尚未写盘的时间。</summary>
    public int ServerSaveIntervalMs { get; set; } = 60_000;

    /// <summary>服务端首次周期存档的错峰窗口（毫秒）。设为 0 可关闭错峰。调高可分散多仓首轮存档负载。</summary>
    public int ServerSaveStaggerMs { get; set; } = 4_000;

    /// <summary>最后一个会话断开后，服务端继续保留玩家仓库的时长（毫秒）。调高可减少短线重连时重开仓库的次数，并延长内存占用。</summary>
    public int PlayerStoreIdleMs { get; set; } = 60_000;

    /// <summary>服务端检查空闲玩家仓库的间隔（毫秒）。调高可减少扫描频率，空闲仓库也可能更晚关闭。</summary>
    public int PlayerStoreCheckIntervalMs { get; set; } = 1_000;

    /// <summary>关闭空闲玩家仓库失败后，再次尝试的间隔（毫秒）。调低可更快重试，也会增加持续失败时的工作量。</summary>
    public int PlayerStoreCloseRetryMs { get; set; } = 60_000;

    /// <summary>世界卸载后，重试关闭尚未保存成功的仓库的间隔（毫秒）。调低可更快补做保存，也会增加持续失败时的工作量。</summary>
    public int StoreCloseRetryMs { get; set; } = 60_000;

    /// <summary>每次 Pump 最多尝试修复的页数，锁竞争也占用此预算。调高可加速修复，并增加单次推进耗时。</summary>
    public int MaxRepairPagesPerPump { get; set; } = 1;

    /// <summary>修复单页时，首次取锁失败后允许重试的次数。调高使暂时繁忙的页更有机会修复，也可能延长报告完成时间。</summary>
    public int MaxRepairPageLockRetries { get; set; } = 2;

    /// <summary>客户端同时等待的请求数上限，也是服务端每个会话待使用的存入授权数上限。调高允许更多操作在途，并增加保留状态与输入的内存。</summary>
    public int MaxPendingNetworkRequests { get; set; } = 16;

    /// <summary>网络入站队列的总上限，也是客户端每次更新最多处理的入站消息数。调高可缓冲更大的突发流量，并增加积压内存和客户端单次处理量。</summary>
    public int MaxInboundNetworkMessages { get; set; } = 512;

    /// <summary>服务端入站队列中，每个连接最多可积压的消息数。调高可吸收单客户端突发消息，也会让一个连接占用更多队列容量。</summary>
    public int MaxInboundNetworkMessagesPerConnection { get; set; } = 16;

    /// <summary>服务端每次更新最多处理的入站消息数。调高可加速清理积压，也会增加单次服务端更新耗时。</summary>
    public int MaxServerInboundMessagesPerUpdate { get; set; } = 16;

    /// <summary>服务端同时排队等待发送的结果传输数上限。调高允许更多结果等待传输，并增加待发送数据的内存。</summary>
    public int MaxNetworkTransfers { get; set; } = 32;

    /// <summary>服务端每次更新最多处理的传输帧数；失效会话的帧也占用此预算。调高可缩短大结果发送时间，同时增加单次更新的网络负载。</summary>
    public int MaxNetworkFramesPerUpdate { get; set; } = 8;

    /// <summary>客户端请求等待握手和完整结果的总时限（毫秒）。调高可容忍更慢的连接，也会更晚释放失联请求的资源。</summary>
    public int NetworkRequestTimeoutMs { get; set; } = 60_000;

    /// <summary>客户端重发握手请求的间隔（毫秒）。调低可更快完成丢包后的握手，并增加重发频率。</summary>
    public int NetworkHelloRetryMs { get; set; } = 2_000;

    /// <summary>客户端查询未结算操作的间隔（毫秒）。调低可更快发现已结算操作，并增加恢复请求频率。</summary>
    public int NetworkRecoveryRetryMs { get; set; } = 2_000;

    /// <summary>网络指令最多可指定的槽位数，受封包格式上限约束。调高允许更大的显式槽位集合，也会增加校验和执行工作。</summary>
    public int MaxRemoteCommandSlots { get; set; } = Network.NetworkSystem.MaxWireSlots;

    /// <summary>无指定槽位的网络取出指令最多可请求的物品数。调高允许单次取出更多物品，并增加执行和交付负载。</summary>
    public int MaxRemoteTakeItems { get; set; } = 4096;

    /// <summary>网络存入与调试填充指令最多可处理的堆叠数；存入同时限制输入列表长度。调高允许更大的单次存入，也会增加暂存内存和执行耗时。</summary>
    public int MaxRemotePutStacks { get; set; } = 4096;

    /// <summary>事务账本停止接收新事务前允许保留的记录数，受文件格式上限约束。调高可容纳更多历史记录，并增加账本内存和写入成本。</summary>
    public int MaxTransactionLedgerEntries { get; set; } = Network.TransactionLedger.MaxFileEntries;

    /// <summary>事务账本接收新事务时的字节预算。已存在的事务仍可在文件上限内结算。调高可容纳更大的结果记录，并增加账本重写成本。</summary>
    public int MaxTransactionLedgerBytes { get; set; } = Storage.StoreRedoJournal.MaxLedgerBytes;

    /// <summary>单机重试待交付物品的间隔（游戏更新次数）。调低可更快重试，同时增加背包检查频率。</summary>
    public int SinglePlayerDeliveryRetryTicks { get; set; } = 60;

    /// <summary>客户端操作日志允许新增待处理操作的数量。调高可容纳更多待恢复操作，同时增加日志重写成本。</summary>
    public int MaxClientPendingOperations { get; set; } = Integration.ClientOperationJournal.MaxFileEntries;

    /// <summary>客户端交付回执的接纳上限。调高可容纳更多未清理回执，同时增加角色存档的体积。</summary>
    public int MaxClientDeliveryReceipts { get; set; } = 32_768;

    internal static StoreConfig Runtime { get; } = new();

    /// <summary>为新仓库复制集成服务配置，让每个仓库独立维护其运行参数。</summary>
    internal StoreConfig Copy() => (StoreConfig)MemberwiseClone();

    /// <summary>将非法配置值钳制到可用范围。</summary>
    internal void Normalize()
    {
        MaxLoadedPages = Math.Max(1, MaxLoadedPages);
        PageIdleUnloadMs = Math.Max(0, PageIdleUnloadMs);
        MaxBatchCommands = Math.Clamp(MaxBatchCommands, 1, 4096);
        MaxControlFlowCommands = Math.Clamp(MaxControlFlowCommands, 1, 4096);
        MaxControlFlowSteps = Math.Max(1, MaxControlFlowSteps);
        MaxConcurrentCommandsPerBatch = Math.Clamp(MaxConcurrentCommandsPerBatch, 1, MaxSupportedConcurrentCommands);
        MaxConcurrentSlotsPerCommand = Math.Clamp(MaxConcurrentSlotsPerCommand, 1, MaxSupportedConcurrentSlots);
        MaxDrainBatches = Math.Max(1, MaxDrainBatches);
        MaxBatchesPerPump = Math.Clamp(MaxBatchesPerPump, 1, 1024);
        MaxCommandPumpMs = Math.Clamp(MaxCommandPumpMs, 1, 60_000);
        MaxCommandPageWorkPerPump = Math.Clamp(MaxCommandPageWorkPerPump, 1, 1_000_000);
        MaxQueuedCommands = Math.Clamp(MaxQueuedCommands, 1, 1_000_000);
        MaxReconcilePagesPerPump = Math.Clamp(MaxReconcilePagesPerPump, 1, 1024);
        FlushLockTimeoutMs = Math.Clamp(FlushLockTimeoutMs, 0, 60_000);
        ServerSaveIntervalMs = Math.Max(1, ServerSaveIntervalMs);
        ServerSaveStaggerMs = Math.Clamp(ServerSaveStaggerMs, 0, ServerSaveIntervalMs);
        PlayerStoreIdleMs = Math.Max(0, PlayerStoreIdleMs);
        PlayerStoreCheckIntervalMs = Math.Max(1, PlayerStoreCheckIntervalMs);
        PlayerStoreCloseRetryMs = Math.Max(1, PlayerStoreCloseRetryMs);
        StoreCloseRetryMs = Math.Max(1, StoreCloseRetryMs);
        MaxRepairPagesPerPump = Math.Clamp(MaxRepairPagesPerPump, 1, 1024);
        MaxRepairPageLockRetries = Math.Max(0, MaxRepairPageLockRetries);
        MaxPendingNetworkRequests = Math.Max(1, MaxPendingNetworkRequests);
        MaxInboundNetworkMessages = Math.Max(1, MaxInboundNetworkMessages);
        MaxInboundNetworkMessagesPerConnection = Math.Clamp(MaxInboundNetworkMessagesPerConnection, 1, MaxInboundNetworkMessages);
        MaxServerInboundMessagesPerUpdate = Math.Max(1, MaxServerInboundMessagesPerUpdate);
        MaxNetworkTransfers = Math.Max(1, MaxNetworkTransfers);
        MaxNetworkFramesPerUpdate = Math.Max(1, MaxNetworkFramesPerUpdate);
        NetworkRequestTimeoutMs = Math.Max(1, NetworkRequestTimeoutMs);
        NetworkHelloRetryMs = Math.Max(1, NetworkHelloRetryMs);
        NetworkRecoveryRetryMs = Math.Max(1, NetworkRecoveryRetryMs);
        SinglePlayerDeliveryRetryTicks = Math.Max(1, SinglePlayerDeliveryRetryTicks);
        MaxClientPendingOperations = Math.Clamp(MaxClientPendingOperations, 1, Integration.ClientOperationJournal.MaxFileEntries);
        MaxClientDeliveryReceipts = Math.Max(2, MaxClientDeliveryReceipts);
        MaxRemoteCommandSlots = Math.Clamp(MaxRemoteCommandSlots, 1, Network.NetworkSystem.MaxWireSlots);
        MaxRemoteTakeItems = Math.Max(1, MaxRemoteTakeItems);
        MaxRemotePutStacks = Math.Max(1, MaxRemotePutStacks);
        MaxTransactionLedgerEntries = Math.Clamp(MaxTransactionLedgerEntries, 1, Network.TransactionLedger.MaxFileEntries);
        MaxTransactionLedgerBytes = Math.Clamp(MaxTransactionLedgerBytes, 1, Storage.StoreRedoJournal.MaxLedgerBytes);
    }
}
