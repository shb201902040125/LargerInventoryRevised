using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using LargerInventory.Backend.Commands;
using LargerInventory.Backend.Repair;
using LargerInventory.Backend.Storage;

namespace LargerInventory.Backend;

/// <summary>
/// 储存实例（非静态）。页缓存 + 类型聚合 + 指令队列 + 执行器。
/// 所有执行都通过 <see cref="Pump"/> 在调用线程（Terraria 主线程）上完成；指令回调同样在该线程执行。
/// 绑定作用域目录时启用每页文件持久化与 store.meta 快速视图。
/// </summary>
public sealed class InventoryStore : IDisposable
{
    public StoreConfig Config { get; }
    public string DirectoryPath { get; }
    public StoreScope Scope { get; }

    internal bool IsDisposed => _disposed;

    internal PageCache PageCache { get; private set; }
    internal TypeIndex TypeIndex { get; private set; }
    internal CommandQueue Queue { get; }
    internal PageLockTable PageLocks { get; } = new();
    internal IPageFileStore FileStore => _fileStore;

    private readonly CommandExecutor _executor;
    private readonly IPageFileStore _fileStore;
    private readonly StoreRedoJournal _journal;
    private byte[] _ledger;
    private volatile int _mainThreadId;
    private volatile bool _insideMainOperation;
    private bool _needsReplay;
    private bool _metadataNeedsRefresh;
    private int _metadataPageCount;
    // Only the owner thread advances pending state under the executor gate. The worker sees snapshots only.
    private volatile PendingCommit _pendingCommit;

    private sealed record StagedCommit(Func<IReadOnlyList<StoreRedoJournal.Write>> Prepare, ItemPage[] Pages, byte[] Ledger);

    private sealed class PendingCommit
    {
        public Action<bool, string> Completed { get; }
        public Command Command { get; set; }
        public Func<CommandResult, byte[]> EncodeLedger { get; set; }
        public bool RollbackOnFailure { get; set; }
        public StagedCommit Staged { get; set; }
        public Task<StoreRedoJournal.CommitResult> Disk { get; set; }

        public PendingCommit(Action<bool, string> completed) => Completed = completed;
    }
    private readonly object _lifecycleGate = new();
    private PageRepairService _repairService;
    private Dictionary<int, PageSummary> _loadedSummaries = new();
    private bool _rebuildAllSummaries;
    private volatile bool _pendingReconcile;
    private volatile bool _reloadCommittedPages;
    private IReadOnlyList<int> _reconcilePages;
    private int _reconcileCursor;
    private int _reconciledCount;

    /// <summary>Persistent page summaries are still being reconciled by Pump.</summary>
    public bool IsPreparing => _pendingReconcile || _reloadCommittedPages;

    internal bool IsBusy => _pendingCommit is not null || _insideMainOperation || IsPreparing || IsRepairRunning || _disposing || _disposed;
    private const string PendingCommitError = "store has a pending transaction commit";

    private const string PreparingError = "store is preparing";
    private bool _started;
    private volatile bool _disposed;
    private volatile bool _disposing;

    public InventoryStore(StoreConfig config = null)
        : this(config, null, StoreScope.World)
    {
    }

    public InventoryStore(StoreConfig config, string storeDirectory, StoreScope scope = StoreScope.World)
    {
        Config = config ?? new StoreConfig();
        Config.Normalize();
        Scope = scope;
        DirectoryPath = storeDirectory;

        if (!string.IsNullOrWhiteSpace(storeDirectory))
        {
            Directory.CreateDirectory(storeDirectory);
            _fileStore = new FilePageFileStore(storeDirectory);
            _journal = new StoreRedoJournal(storeDirectory);
            _journal.Recover();
            _ledger = StoreRedoJournal.ReadOptional(Path.Combine(storeDirectory, StoreRedoJournal.LedgerName));
        }
        else
        {
            _fileStore = new NullPageFileStore();
            Config.EnablePageEviction = false;
        }

        PageCache = new PageCache(Config, _fileStore, PageLocks);
        TypeIndex = new TypeIndex();
        Queue = new CommandQueue();
        _executor = new CommandExecutor(this, Queue);

        LoadPersistentState();
    }

    public void Start()
    {
        _started = true;
    }

    /// <summary>
    /// 压入一条指令。完成通知只通过 Command.Callback。若队列已满或存储已销毁，指令立即以失败结束，
    /// 且 Put 会返回全部物品，调用方可据此补偿。
    /// </summary>
    public void Push(Command command)
    {
        if (command is null)
        {
            throw new ArgumentNullException(nameof(command));
        }
        if (!command.TryMarkSubmitted())
            return;

        CommandStatus? rejection = null;
        lock (_lifecycleGate)
        {
            if (!_started)
            {
                Start();
            }

            if (_disposed || _disposing)
            {
                rejection = CommandStatus.InternalError;
            }
            else if (Integration.InventorySystem.CommandsPaused)
            {
                rejection = CommandStatus.ManualInterrupt;
            }
            else if (IsPreparing)
            {
                rejection = CommandStatus.Preparing;
            }
            else if (_pendingCommit is not null)
            {
                rejection = CommandStatus.PageBusy;
            }
            else if (!Queue.TryPush(command, Config.MaxQueuedCommands))
            {
                rejection = CommandStatus.Throttled;
            }
        }

        if (rejection.HasValue)
        {
            RejectCommand(command, rejection.Value);
        }
    }

    /// <summary>
    /// 批量压入：整批连续入队。若容量不足则整批拒绝，避免部分入队。
    /// </summary>
    public void PushBatch(IReadOnlyList<Command> commands)
    {
        if (commands is null)
        {
            throw new ArgumentNullException(nameof(commands));
        }
        foreach (var command in commands)
            ArgumentNullException.ThrowIfNull(command);
        var submitted = new List<Command>(commands.Count);
        foreach (var command in commands)
            if (command.TryMarkSubmitted())
                submitted.Add(command);
        if (submitted.Count == 0)
            return;

        CommandStatus? rejection = null;
        lock (_lifecycleGate)
        {
            if (!_started)
            {
                Start();
            }

            if (_disposed || _disposing)
            {
                rejection = CommandStatus.InternalError;
            }
            else if (Integration.InventorySystem.CommandsPaused)
            {
                rejection = CommandStatus.ManualInterrupt;
            }
            else if (IsPreparing)
            {
                rejection = CommandStatus.Preparing;
            }
            else if (_pendingCommit is not null)
            {
                rejection = CommandStatus.PageBusy;
            }
            else if (!Queue.TryPushBatch(submitted, Config.MaxQueuedCommands))
            {
                rejection = CommandStatus.Throttled;
            }
        }

        if (rejection.HasValue)
        {
            foreach (var command in submitted)
            {
                RejectCommand(command, rejection.Value);
            }
        }
    }

    /// <summary>
    /// 在调用线程（Terraria 主线程）执行到期指令，并调用其回调。应在每帧调用。
    /// </summary>
    public int Pump()
    {
        if (_disposed || _disposing)
        {
            return 0;
        }

        if (_insideMainOperation || !_executor.TryAcquireForSave(0))
            return 0;
        Action notify = null;
        try
        {
            if (_disposed || _disposing)
                return 0;
            if (_pendingCommit is not null)
            {
                // Do not run ordinary commands, repair, or eviction until the transaction outcome is known.
                if (IsMainThread)
                    SettlePendingCommit(0, out notify, out _);
                return 0;
            }

            _mainThreadId = Environment.CurrentManagedThreadId;
            _insideMainOperation = true;
            EnsureReplayed();
            if (_reloadCommittedPages)
                ReloadCommittedPages();
            if (!ReconcilePages(Math.Max(1, Config.MaxReconcilePagesPerPump)))
                return 0;
            _repairService?.Pump();
            EnsureReplayed();
            var completed = _started ? _executor.Pump() : 0;
            CaptureDirtyPageSnapshots();
            PageCache.TrimToLimit();
            return completed;
        }
        finally
        {
            _insideMainOperation = false;
            _executor.ReleaseAfterSave();
            notify?.Invoke();
        }
    }

    public bool StartPageRepair(Action<int> pageChanged, Action<RepairReport> completed, out string error)
    {
        if (Integration.InventorySystem.Instance is not null && Terraria.Main.netMode != Terraria.ID.NetmodeID.SinglePlayer)
        {
            error = "page repair is available only in singleplayer";
            return false;
        }
        error = "store is unavailable or busy";
        if (_disposed || _disposing || _insideMainOperation || _pendingCommit is not null || !_executor.TryAcquireForSave(0))
            return false;
        try
        {
            if (_disposed || _disposing || _pendingCommit is not null)
                return false;
            EnsureReplayed();
            _repairService ??= new PageRepairService(this);
            return _repairService.Start(pageChanged, completed, out error);
        }
        catch (Exception e)
        {
            error = e.Message;
            return false;
        }
        finally { _executor.ReleaseAfterSave(); }
    }

    public void CancelPageRepair()
    {
        _repairService?.Cancel();
    }

    /// <summary>人工干预：清空未执行指令，统一返回 ManualInterrupt。</summary>
    public void AbortCommands(CommandStatus status)
    {
        _executor.AbortQueued(status);
    }

    public RepairReport GetRepairSnapshot()
    {
        return _repairService?.Snapshot();
    }

    public bool IsRepairRunning => _repairService?.IsRunning == true;

    /// <summary>Commits every dirty page from main-thread snapshots, and the matching metadata.</summary>
    public bool FlushForSave(out string error)
    {
        error = "store is disposed or executor busy";
        if (_disposed || _disposing || (_insideMainOperation && IsMainThread) || !_executor.TryAcquireForSave(Config.FlushLockTimeoutMs))
            return false;
        Action notify = null;
        try
        {
            if (_disposed || _disposing)
                return false;
            if (!SettlePendingCommit(Config.FlushLockTimeoutMs, out notify, out error))
                return false;
            FlushCore();
            error = null;
            return true;
        }
        catch (Exception e)
        {
            error = e.Message;
            Log.Error("FlushForSave failed.", e);
            return false;
        }
        finally
        {
            _insideMainOperation = false;
            _executor.ReleaseAfterSave();
            notify?.Invoke();
        }
    }

    // Caller holds the executor gate and has settled any pending transaction.
    private void FlushCore()
    {
        if (IsMainThread)
        {
            _insideMainOperation = true;
            CaptureDirtyPageSnapshots();
        }
        CommitCore(null);
    }

    private bool IsMainThread => _mainThreadId == Environment.CurrentManagedThreadId;

    internal void CaptureDirtyPageSnapshots() => PageCache.CaptureDirtySnapshots();

    internal byte[] LoadTransactionLedger() => _ledger is null ? null : (byte[])_ledger.Clone();

    internal void SchedulePeriodicSave()
    {
        // Pump settles the in-flight commit. A busy store is retried on the next interval.
        if (_journal is null || IsBusy)
            return;
        if (!BeginCommitAsync(null, null, null, (success, error) =>
        {
            if (!success)
                Log.Error($"Periodic store save failed ({DirectoryPath}): {error}");
        }, out var error, saveOnly: true))
            Log.Warn($"Periodic store save could not start ({DirectoryPath}): {error}");
    }

    /// <summary>Accepts a ledger update on the game thread and reports its durable outcome there.</summary>
    internal bool CommitTransactionLedgerAsync(byte[] ledger, Action<bool, string> completed, out string error) =>
        BeginCommitAsync(ledger, null, null, completed, out error);

    /// <summary>Stages a transaction on the game thread and reports its durable outcome there.</summary>
    /// <remarks>If earlier dirty pages must be saved first, the encoder runs on a later owner-thread pump.
    /// An accepted operation calls completed exactly once, after leaving the execution gate.</remarks>
    internal bool TryExecuteTransactionAsync(Command command, Func<CommandResult, byte[]> encodeLedger,
        Action<bool, string> completed, out string error)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(encodeLedger);
        return BeginCommitAsync(null, command, encodeLedger, completed, out error);
    }

    private bool BeginCommitAsync(byte[] ledger, Command command, Func<CommandResult, byte[]> encodeLedger,
        Action<bool, string> completed, out string error, bool saveOnly = false)
    {
        ArgumentNullException.ThrowIfNull(completed);
        error = "persistent store is unavailable or busy";
        if ((!saveOnly && ledger is null && command is null) || _journal is null || _disposed || _disposing ||
            _insideMainOperation || _pendingCommit is not null ||
            (Integration.InventorySystem.Instance is not null && !Terraria.Program.IsMainThread) ||
            !_executor.TryAcquireForSave(0))
            return false;

        var pending = new PendingCommit(completed);
        try
        {
            if (IsPreparing)
            {
                error = PreparingError;
                return false;
            }
            if (_disposed || _disposing || _pendingCommit is not null || IsRepairRunning ||
                (_mainThreadId != 0 && !IsMainThread))
                return false;

            if (saveOnly && !HasSaveWork)
            {
                error = null;
                return true;
            }

            _mainThreadId = Environment.CurrentManagedThreadId;
            _insideMainOperation = true;
            _pendingCommit = pending;
            if (command is not null && (PageCache.HasDirtyPages || _needsReplay))
            {
                // Do not let a later rollback discard work already acknowledged to ordinary callers.
                pending.Command = command;
                pending.EncodeLedger = encodeLedger;
                StartDiskCommit(pending, null);
            }
            else if (command is not null)
            {
                StartTransactionCommit(pending, command, encodeLedger);
            }
            else
            {
                StartDiskCommit(pending, ledger);
            }
            error = null;
            return true;
        }
        catch (Exception e)
        {
            error = HandleCommitFailure(pending, e);
            _pendingCommit = null;
            return false;
        }
        finally
        {
            _insideMainOperation = false;
            _executor.ReleaseAfterSave();
        }
    }

    private void StartTransactionCommit(PendingCommit pending, Command command, Func<CommandResult, byte[]> encodeLedger)
    {
        pending.RollbackOnFailure = true;
        var result = _executor.ExecuteTransactionCommand(command);
        StartDiskCommit(pending, encodeLedger(result) ?? throw new InvalidOperationException("Transaction ledger was not encoded."));
    }

    private void StartDiskCommit(PendingCommit pending, byte[] ledger)
    {
        CaptureDirtyPageSnapshots();
        pending.Staged = StageCommit(ledger);
        pending.Disk = _journal.CommitPreparedAsync(pending.Staged.Prepare);
    }

    // Returns false only if the pending operation could not be settled yet. An ordinary save may still
    // proceed once a failed transaction has rolled back. The transaction's own outcome goes to its caller.
    private bool SettlePendingCommit(int timeoutMs, out Action notify, out string error)
    {
        notify = null;
        error = null;
        var pending = _pendingCommit;
        if (pending is null)
            return true;
        error = PendingCommitError;
        if (!IsMainThread)
            return false;

        var started = Environment.TickCount64;
        _insideMainOperation = true;
        try
        {
            while (true)
            {
                var remaining = (int)Math.Max(0, timeoutMs - (Environment.TickCount64 - started));
                if (!WaitForDisk(pending.Disk, remaining))
                    return false;

                var durable = pending.Disk.GetAwaiter().GetResult();
                ApplyCommit(pending.Staged, durable.Published);
                durable.ReportDiagnostics();

                if (pending.Command is not { } command)
                    break;

                EnsureReplayed();
                var encodeLedger = pending.EncodeLedger;
                pending.Command = null;
                pending.EncodeLedger = null;
                StartTransactionCommit(pending, command, encodeLedger);
            }
        }
        catch (Exception e)
        {
            var failure = HandleCommitFailure(pending, e);
            _pendingCommit = null;
            notify = () => NotifyCommit(pending.Completed, false, failure);
            error = null;
            return true;
        }

        _pendingCommit = null;
        notify = () => NotifyCommit(pending.Completed, true, null);
        error = null;
        return true;
    }

    private static bool WaitForDisk(Task<StoreRedoJournal.CommitResult> disk, int timeoutMs)
    {
        if (disk.IsCompleted)
            return true;
        if (timeoutMs == 0)
            return false;
        try { return disk.Wait(timeoutMs); }
        catch (AggregateException) { return true; } // GetAwaiter surfaces the original exception.
    }

    private string HandleCommitFailure(PendingCommit pending, Exception failure)
    {
        var error = failure.Message;
        if (pending.RollbackOnFailure)
        {
            try { ReloadCommittedPages(); }
            catch (Exception rollbackError)
            {
                // Keep this instance unavailable unless its committed state can be loaded again.
                _reloadCommittedPages = true;
                error += " Rollback could not reload the committed store: " + rollbackError.Message;
            }
        }
        ReportCommitError("Transaction commit failed.", failure);
        return error;
    }

    private static void NotifyCommit(Action<bool, string> completed, bool success, string error)
    {
        try { completed(success, error); }
        catch (Exception e) { ReportCommitError("Transaction completion callback failed.", e); }
    }

    private static void ReportCommitError(string message, Exception error)
    {
        try { Log.Error(message, error); }
        catch (Exception) { } // Reporting must not change commit ownership or outcome.
    }

    internal bool CommitTransactionLedger(byte[] ledger, out string error)
    {
        error = "persistent store is unavailable or busy";
        if (ledger is null || _journal is null || _disposed || _disposing || _insideMainOperation || _pendingCommit is not null ||
            !_executor.TryAcquireForSave(Config.FlushLockTimeoutMs))
            return false;
        try
        {
            if (_disposed || _disposing || _pendingCommit is not null)
                return false;
            _insideMainOperation = true;
            CommitCore(ledger);
            error = null;
            return true;
        }
        catch (Exception e) { error = e.Message; return false; }
        finally
        {
            _insideMainOperation = false;
            _executor.ReleaseAfterSave();
        }
    }

    internal bool TryExecuteTransaction(Command command, Func<CommandResult, byte[]> encodeLedger, out string error)
    {
        error = "persistent store is unavailable or busy";
        if (_journal is null || _disposed || _disposing || _insideMainOperation || _pendingCommit is not null ||
            !_executor.TryAcquireForSave(Config.FlushLockTimeoutMs))
            return false;
        var executeStarted = false;
        try
        {
            if (_disposed || _disposing || _pendingCommit is not null)
                return false;
            _mainThreadId = Environment.CurrentManagedThreadId;
            _insideMainOperation = true;
            if (IsPreparing)
            {
                error = PreparingError;
                return false;
            }
            EnsureReplayed();
            // Save earlier ordinary commands separately, so rollback never discards acknowledged work.
            if (PageCache.HasDirtyPages)
            {
                CaptureDirtyPageSnapshots();
                CommitCore(null);
                EnsureReplayed();
            }
            executeStarted = true;
            var result = _executor.ExecuteTransactionCommand(command);
            CaptureDirtyPageSnapshots();
            CommitCore(encodeLedger(result) ?? throw new InvalidOperationException("Transaction ledger was not encoded."));
            error = null;
            return true;
        }
        catch (Exception e)
        {
            if (executeStarted)
            {
                ReloadCommittedPages();
            }
            error = e.Message;
            Log.Error("Transaction commit failed.", e);
            return false;
        }
        finally
        {
            _insideMainOperation = false;
            _executor.ReleaseAfterSave();
        }
    }

    private void ReloadCommittedPages()
    {
        _reloadCommittedPages = true;
        PageCache = new PageCache(Config, _fileStore, PageLocks);
        TypeIndex = new TypeIndex();
        LoadPersistentState();
        _reloadCommittedPages = false;
    }

    private void EnsureReplayed()
    {
        if (!_needsReplay)
            return;
        _journal.Recover();
        _needsReplay = false;
    }

    private void CommitCore(byte[] ledger)
    {
        if (_journal is null)
            return;
        // A save during reconciliation may replay committed work but must never publish partial metadata.
        if (IsPreparing)
        {
            EnsureReplayed();
            if (!PageCache.HasDirtyPages && ledger is null)
                return;
            throw new InvalidOperationException(PreparingError);
        }
        if (!HasSaveWork && ledger is null)
            return;
        var staged = StageCommit(ledger);
        ApplyCommit(staged, _journal.CommitPrepared(staged.Prepare));
    }

    private bool HasSaveWork => PageCache.HasDirtyPages || NeedsMetadataRefresh || _needsReplay;

    private bool NeedsMetadataRefresh => _metadataNeedsRefresh || PageCache.AllocatedPages.Count != _metadataPageCount;

    private StagedCommit StageCommit(byte[] ledger,
        IReadOnlyList<StoreRedoJournal.Write> initialWrites = null)
    {
        if (ledger is not null && ledger.Length > StoreRedoJournal.MaxLedgerBytes)
            throw new InvalidDataException("Transaction ledger exceeds its size limit.");

        var retained = (byte[])ledger?.Clone();
        var (pages, pageFiles) = PageCache.FreezeDirtyPages();
        var metadata = NeedsMetadataRefresh || pages.Length > 0 || ledger is not null || initialWrites is not null
            ? StoreMeta.BuildSnapshot(PageCache.AllocatedPages)
            : null;
        var files = (FilePageFileStore)_fileStore;
        var directory = DirectoryPath;

        // The journal delegate owns only detached buffers, primitive tags and path information.
        // ItemPage references are retained separately for acknowledgement under the executor gate.
        IReadOnlyList<StoreRedoJournal.Write> Prepare()
        {
            var writes = initialWrites is null ? new List<StoreRedoJournal.Write>() : new(initialWrites);
            foreach (var snapshot in pageFiles)
                files.StageNormalWrite(snapshot, writes);
            if (metadata is not null)
                StoreMeta.Stage(directory, metadata, writes);
            if (retained is not null)
                writes.Add(new(StoreRedoJournal.LedgerName, retained));
            return writes;
        }

        return new StagedCommit(Prepare, pages, retained);
    }

    private void ApplyCommit(StagedCommit staged, bool published)
    {
        _needsReplay = !published;
        _metadataNeedsRefresh = false;
        _metadataPageCount = PageCache.AllocatedPages.Count;
        PageCache.AcknowledgeCommit(staged.Pages);
        if (staged.Ledger is not null)
            _ledger = staged.Ledger;
    }

    internal void CommitRepairPage(int index, byte[] contents, PageSummary summary, bool deleteBackup)
    {
        EnsureReplayed();
        var page = PageCache.Directory.GetOrCreate(index);
        var previous = new PageSummary();
        previous.CopyFrom(page.Summary);
        var wasFailed = page.LoadFailed;
        var target = FilePageFileStore.FileName(index);
        var writes = new List<StoreRedoJournal.Write> { new(target, contents) };
        if (deleteBackup)
            writes.Add(new(target + ".bak", null));
        try
        {
            page.Summary.CopyFrom(summary);
            page.LoadFailed = false;
            // Any in-memory dirty pages represented in this metadata belong to the same commit.
            CaptureDirtyPageSnapshots();
            var staged = StageCommit(null, initialWrites: writes);
            ApplyCommit(staged, _journal.CommitPrepared(staged.Prepare));
        }
        catch
        {
            page.Summary.CopyFrom(previous);
            page.LoadFailed = wasFailed;
            throw;
        }
        if (Config.EnableGlobalAggregate)
            TypeIndex.UpdatePage(index, page.Indexed ? previous.Types : new Dictionary<int, TypeStat>(), summary.Types);
        page.Indexed = true;
        page.KnownNonEmpty = summary.OccupiedStacks > 0;
        page.ReleaseSlots();
        PageCache.ClearBackupSource(index);
    }

    internal ItemPage EnsurePage(int pageIndex)
    {
        // A repair callback may refresh a page before the remainder of Pump runs.
        EnsureReplayed();
        var wasLoaded = PageCache.TryGetLoaded(pageIndex, out _);
        var existed = PageCache.Directory.TryGet(pageIndex, out var existingPage);
        var wasFailed = existed && existingPage.LoadFailed;
        var pageWasIndexed = existed && existingPage.Indexed;
        var previousTypes = pageWasIndexed
            ? existingPage.Summary.SnapshotTypes()
            : new Dictionary<int, TypeStat>();

        ItemPage page;
        try
        {
            page = PageCache.EnsureLoaded(pageIndex);
        }
        catch (PageLoadException)
        {
            if (!existed || (!wasFailed && existingPage.LoadFailed))
                _metadataNeedsRefresh = true;
            // 页不可用：从类型索引中移除其旧摘要，查询不再把无法访问的物品算作可用。
            // 页本身仍保留在目录与 store.meta 中，索引不会被分配器复用。
            if (pageWasIndexed && Config.EnableGlobalAggregate)
            {
                TypeIndex.UpdatePage(pageIndex, previousTypes, new Dictionary<int, TypeStat>());
                existingPage.Indexed = false;
            }

            throw;
        }

        // Loading can replace a stale summary or clear a previously unavailable state.
        if (!wasLoaded)
            _metadataNeedsRefresh = true;

        if (Config.EnableGlobalAggregate)
        {
            if (!page.Indexed)
            {
                TypeIndex.UpdatePage(pageIndex, new Dictionary<int, TypeStat>(), page.Summary.Types);
                page.Indexed = true;
            }
            else if (!wasLoaded)
            {
                // 从页文件重建摘要后，用实际 Item 统计替换 store.meta 中的旧索引。
                TypeIndex.UpdatePage(pageIndex, previousTypes, page.Summary.Types);
            }
        }

        return page;
    }

    internal ItemPage AllocatePage(bool allowCapacityFailure = false)
    {
        var page = PageCache.AllocatePage(allowCapacityFailure);
        if (page is not null)
            page.Indexed = true;
        return page;
    }

    internal void PinPage(int pageIndex)
    {
        PageCache.Pin(pageIndex);
    }

    internal void MarkDirty(ItemPage page)
    {
        PageCache.MarkDirty(page);
    }

    /// <summary>重算受影响页的摘要，并更新全局类型聚合。</summary>
    internal void SyncSummaries(HashSet<int> affectedPages)
    {
        foreach (var pageIndex in affectedPages)
        {
            if (!PageCache.Directory.TryGet(pageIndex, out var page) || !page.Loaded)
            {
                continue;
            }

            var before = page.Summary.SnapshotTypes();
            page.Summary.RebuildFrom(page.Slots);
            page.Summary.Version++;
            page.KnownNonEmpty = page.Summary.OccupiedStacks > 0;
            PageCache.MarkDirty(page);

            if (Config.EnableGlobalAggregate)
            {
                TypeIndex.UpdatePage(pageIndex, before, page.Summary.Types);
            }
        }
    }

    /// <summary>
    /// 读取 store.meta（纯数据，允许在 tModLoader 的异步加载线程执行）。页文件扫描与 Item 反序列化
    /// 推迟到主线程的 <see cref="Pump"/>，避免在异步加载线程上调用 ItemIO。
    /// </summary>
    private void LoadPersistentState()
    {
        if (!_fileStore.Available)
        {
            return;
        }

        var status = StoreMeta.Load(DirectoryPath, out var summaries, out var error);
        if (status == StoreMetaStatus.Invalid)
        {
            Log.Error($"store.meta unusable; rebuilding page index from page files. {error}");
        }

        _loadedSummaries = summaries;
        _rebuildAllSummaries = status != StoreMetaStatus.Valid;
        _metadataNeedsRefresh = _rebuildAllSummaries;
        _metadataPageCount = summaries.Count;
        _reconcilePages = null;
        _reconcileCursor = 0;
        _reconciledCount = 0;
        _pendingReconcile = true;

        foreach (var pair in summaries)
        {
            var page = PageCache.Directory.GetOrCreate(pair.Key);
            page.Summary.CopyFrom(pair.Value);
            page.KnownNonEmpty = pair.Value.OccupiedStacks > 0;
            page.LoadFailed = pair.Value.Unavailable;
            page.Indexed = !page.LoadFailed;

            // 不可用页不计入类型索引，避免查询/取出把无法访问的物品算作可用；
            // 页索引仍保留在目录中，禁止被分配器复用。
            if (Config.EnableGlobalAggregate && !page.LoadFailed)
            {
                TypeIndex.UpdatePage(pair.Key, new Dictionary<int, TypeStat>(), page.Summary.Types);
            }
        }
    }

    /// <summary>Reconciles a bounded number of page indices on the main thread. Returns true once ready.</summary>
    private bool ReconcilePages(int budget)
    {
        if (!IsPreparing)
            return true;

        _reconcilePages ??= _fileStore.ListPages();
        while (budget-- > 0 && _reconcileCursor < _reconcilePages.Count)
        {
            var pageIndex = _reconcilePages[_reconcileCursor];
            if (_rebuildAllSummaries || !_loadedSummaries.ContainsKey(pageIndex))
            {
                try
                {
                    EnsurePage(pageIndex);
                    _reconciledCount++;
                    PageCache.TrimToLimit();
                }
                catch (PageBusyException)
                {
                    return false; // Retry the same page after its exclusive lease is released.
                }
                catch (PageLoadException e)
                {
                    Log.Error($"Page {pageIndex} could not be loaded and will be skipped.", e);
                }
            }
            _reconcileCursor++;
        }

        if (_reconcileCursor < _reconcilePages.Count)
            return false;

        if (_reconciledCount > 0)
            Log.Info($"Store {DirectoryPath}: {_loadedSummaries.Count} page(s) from store.meta, {_reconciledCount} page(s) reindexed.");
        _reconcilePages = null;
        _loadedSummaries.Clear();
        _pendingReconcile = false;
        return true;
    }

    private static void RejectCommand(Command command, CommandStatus status)
    {
        var result = Command.FailUnexecuted(command, status);
        command.Complete(result);
        try
        {
            command.Callback?.Invoke(result);
        }
        catch (Exception e)
        {
            Log.Error("Rejected command callback failed.", e);
        }
    }

    public void Dispose()
    {
        if (!TryDispose(out var error))
            Log.Error("Dispose FlushForSave failed: " + error);
    }

    internal bool TryDispose(out string error)
    {
        error = null;
        lock (_lifecycleGate)
        {
            if (_disposed)
                return true;
            if (_disposing || (_insideMainOperation && IsMainThread))
            {
                error = "store is busy";
                return false;
            }
            _disposing = true;
        }

        Action notify = null;
        try
        {
            if (!_executor.TryAcquireForSave(Config.FlushLockTimeoutMs))
            {
                error = "executor is busy";
                return false;
            }
            try
            {
                if (!SettlePendingCommit(Config.FlushLockTimeoutMs, out notify, out error))
                    return false;
                _repairService?.Dispose();
                _repairService = null;
                if (IsMainThread || Integration.InventorySystem.Instance is null)
                {
                    EnsureReplayed();
                    // Commands accepted before a transaction rollback must still see a complete index.
                    if (Queue.Count > 0)
                    {
                        if (_reloadCommittedPages)
                            ReloadCommittedPages();
                        if (!ReconcilePages(int.MaxValue))
                        {
                            error = "page reconciliation is busy";
                            return false;
                        }
                    }
                    _executor.Drain();
                }
                else
                    _executor.AbortQueued(CommandStatus.ManualInterrupt);
                FlushCore();
                // A stale save caller waiting on this gate must see the disposed state before writing.
                _disposed = true;
                return true;
            }
            finally
            {
                _insideMainOperation = false;
                _executor.ReleaseAfterSave();
            }
        }
        catch (Exception e)
        {
            error = e.Message;
            return false;
        }
        finally
        {
            lock (_lifecycleGate)
                _disposing = false;
            notify?.Invoke();
        }
    }

}
