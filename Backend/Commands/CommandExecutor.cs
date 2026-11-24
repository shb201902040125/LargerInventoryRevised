using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Stopwatch = System.Diagnostics.Stopwatch;
using LargerInventory.Backend.Storage;
using Terraria;
using Terraria.ModLoader;

namespace LargerInventory.Backend.Commands;

/// <summary>
/// 调用 <see cref="Pump"/> / <see cref="Drain"/> 的线程负责页加载、发布槽位更新和结果回调。
/// 批内独立槽位操作可在工作线程执行。执行与存档共用 <see cref="_executeGate"/>。
/// </summary>
internal sealed partial class CommandExecutor
{
    private readonly InventoryStore _store;
    private readonly CommandQueue _queue;
    private readonly object _executeGate = new();
    private volatile int _executingThreadId;
    // Only the executor owner accesses these sets. FIFO commands share the batch's page leases.
    private readonly HashSet<int> _batchPages = new();
    private readonly HashSet<int> _reservedSlots = new();
    private bool _pageBusyHit;
    private bool _pageLoadFailedHit;
    private long _pageWork;

    public CommandExecutor(InventoryStore store, CommandQueue queue)
    {
        _store = store;
        _queue = queue;
    }

    /// <summary>
    /// 在当前线程执行到期指令。若执行锁被占用（例如另一线程正在存档），本次直接跳过，不阻塞调用线程。
    /// </summary>
    public int Pump()
    {
        if (_executingThreadId == Environment.CurrentManagedThreadId)
        {
            return 0;
        }

        if (!Monitor.TryEnter(_executeGate))
        {
            return 0;
        }

        try
        {
            _executingThreadId = Environment.CurrentManagedThreadId;
            var started = Stopwatch.GetTimestamp();
            var timeBudgetMs = _store.Config.MaxCommandPumpMs;
            var pageBudget = _store.Config.MaxCommandPageWorkPerPump;
            _pageWork = 0;
            var executed = 0;
            for (var round = 0; round < _store.Config.MaxBatchesPerPump; round++)
            {
                // Leave untouched work in the queue. A selected batch completes with its cleanup
                // and callbacks before yielding, and the first ready batch always gets a turn.
                if (Integration.InventorySystem.CommandsPaused ||
                    !_queue.TryTakeBatch(PageCache.NowMs(), _store.Config.MaxBatchCommands, out var batch))
                {
                    break;
                }

                ExecuteBatch(batch);
                executed += batch.Count == 1 && batch[0].InputPayload is ControlTransactionPayload transaction
                    ? transaction.Commands.Length : batch.Count;
                if (_pageWork >= pageBudget || Stopwatch.GetElapsedTime(started).TotalMilliseconds >= timeBudgetMs)
                {
                    break;
                }
            }

            return executed;
        }
        finally
        {
            _executingThreadId = 0;
            Monitor.Exit(_executeGate);
        }
    }

    /// <summary>
    /// 销毁前在当前线程执行全部待处理指令。仍无法完成的指令以 InternalError 明确失败，
    /// Put 会返回全部物品，调用方可据此补偿，不会静默丢失。
    /// </summary>
    public void Drain()
    {
        if (_executingThreadId == Environment.CurrentManagedThreadId)
        {
            return;
        }

        if (!Monitor.TryEnter(_executeGate, _store.Config.FlushLockTimeoutMs))
        {
            // 存档线程正持有执行锁时，不要无限期阻塞主线程；剩余指令明确失败。
            foreach (var pending in _queue.TakeAll())
            {
                CompleteAndNotify(pending, Command.FailUnexecuted(pending, CommandStatus.InternalError));
            }

            return;
        }

        try
        {
            _executingThreadId = Environment.CurrentManagedThreadId;

            var budget = _store.Config.MaxDrainBatches;
            while (budget-- > 0 && _queue.Count > 0)
            {
                if (!_queue.TryTakeBatch(long.MaxValue, _store.Config.MaxBatchCommands, out var batch))
                {
                    break;
                }

                ExecuteBatch(batch);
            }

            foreach (var command in _queue.TakeAll())
            {
                CompleteAndNotify(command, Command.FailUnexecuted(command, CommandStatus.InternalError));
            }
        }
        finally
        {
            _executingThreadId = 0;
            Monitor.Exit(_executeGate);
        }
    }

    /// <summary>
    /// 存档屏障：在超时时间内获取执行锁。返回 false 表示执行锁被占用或发生重入，调用方必须视为存档失败。
    /// </summary>
    public bool TryAcquireForSave(int timeoutMs)
    {
        if (ReferenceEquals(_workerExecutor, this) || _executingThreadId == Environment.CurrentManagedThreadId)
        {
            return false;
        }

        return Monitor.TryEnter(_executeGate, timeoutMs);
    }

    public void ReleaseAfterSave()
    {
        Monitor.Exit(_executeGate);
    }

    /// <summary>人工暂停/销毁时清空队列，所有未执行指令明确失败（Put 返回全部物品）。</summary>
    public void AbortQueued(CommandStatus status)
    {
        foreach (var command in _queue.TakeAll())
        {
            CompleteAndNotify(command, Command.FailUnexecuted(command, status));
        }
    }

    private Dictionary<int, Item> _capturedOriginalSlots;

    internal CommandResult ExecuteTransactionCommand(Command command, Action<int, Item> captureOriginalSlot = null)
    {
        if (command.Kind is CommandKind.Label or CommandKind.Br or CommandKind.TransactionAction or CommandKind.ControlTransaction)
            return Command.FailUnexecuted(command, CommandStatus.InvalidArg);

        if (!Monitor.IsEntered(_executeGate) || _executingThreadId != 0)
        {
            return Command.FailUnexecuted(command, CommandStatus.PageBusy);
        }

        if (!command.TryMarkSubmitted())
        {
            return CommandResult.Fail(CommandStatus.Cancelled);
        }

        _executingThreadId = Environment.CurrentManagedThreadId;
        _capturedOriginalSlots = captureOriginalSlot is null ? null : new();
        try
        {
            ExecuteBatch(new List<Command> { command }, notify: false);
            if (_capturedOriginalSlots is not null)
                foreach (var (slot, item) in _capturedOriginalSlots)
                    if (_store.PageCache.Directory.TryGet(PageOf(slot), out var page) && page.Loaded &&
                        !ReferenceEquals(page.Slots[SlotOf(slot)], item))
                        captureOriginalSlot(slot, item.IsAir ? new Item() : item.Clone());
            return command.Result;
        }
        finally
        {
            _capturedOriginalSlots = null;
            _executingThreadId = 0;
        }
    }

    private void ExecuteBatch(List<Command> batch, bool notify = true)
    {
        if (batch.Count == 1 && batch[0].Kind == CommandKind.ControlTransaction && batch[0].InputPayload is ControlTransactionPayload transaction)
        {
            ExecuteControlBatch(transaction.Commands, transaction.Settle, batch[0]);
            return;
        }
        if (batch.Count > 1 && batch[0].Kind is CommandKind.Label or CommandKind.Br)
        {
            ExecuteControlBatch(batch);
            return;
        }

        var pinnedPages = new HashSet<int>();
        var touchedPages = new HashSet<int>();
        var hasTake = batch.Exists(command => command.Kind == CommandKind.Take);
        if (hasTake) BeginTakeBatch(touchedPages, pinnedPages);
        Dictionary<Command, List<Item>> deliveries = null;
        var failure = CommandStatus.Ok;
        var index = 0;
        try
        {
            for (; index < batch.Count; index++)
            {
                if (Integration.InventorySystem.CommandsPaused)
                {
                    failure = CommandStatus.ManualInterrupt;
                    break;
                }
                var parallelCount = TryExecuteConcurrentWave(batch, index, pinnedPages, touchedPages);
                if (parallelCount != 0)
                {
                    index += parallelCount - 1;
                    continue;
                }
                var command = batch[index];
                var result = ExecuteControlInstruction(command, hasTake, touchedPages, pinnedPages);
                CompleteBatchInstruction(command, result);
            }
            if (hasTake && failure == CommandStatus.Ok)
                deliveries = ConfirmReservations(touchedPages);
        }
        catch (Exception error)
        {
            Debug.Error_Internal(error);
            failure = CommandStatus.InternalError;
        }
        finally
        {
            try
            {
                if (hasTake && failure != CommandStatus.Ok)
                    RestoreControlJournal(0);
                if (hasTake)
                {
                    foreach (var command in batch)
                    {
                        if (failure != CommandStatus.Ok)
                            command.Complete(Command.FailUnexecuted(command, failure | CommandStatus.RolledBack));
                        else if (command.Kind == CommandKind.Take)
                            command.Complete(new CommandResult(_takeBatchResults[command].Status,
                                deliveries.GetValueOrDefault(command) ?? new List<Item>()));
                        else
                            command.Complete(_takeBatchResults[command]);
                    }
                }
                else if (failure != CommandStatus.Ok)
                {
                    for (; index < batch.Count; index++)
                        if (!batch[index].Done.IsSet)
                            batch[index].Complete(Command.FailUnexecuted(batch[index], failure));
                }
            }
            finally
            {
                if (hasTake) EndTakeBatch();
                FinishBatch(touchedPages);
            }
        }

        if (!notify) return;

        foreach (var command in batch)
        {
            NotifyCallback(command);
        }
    }

    private void FinishBatch(HashSet<int> touchedPages)
    {
        try
        {
            _store.SyncSummaries(touchedPages);
            _store.CaptureDirtyPageSnapshots();
        }
        finally
        {
            try
            {
                _store.PageCache.UnpinAll();
            }
            finally
            {
                foreach (var pageIndex in _batchPages)
                    _store.PageLocks.ExitUse(pageIndex);
                _batchPages.Clear();
            }
        }
    }

    private bool TryReserveStrongSlots(AccessSet access)
    {
        // Acquire every required page before a strong command can modify any slot.
        var acquired = new List<int>();
        foreach (var pageIndex in access.Slots.Select(PageOf).Distinct().Order())
        {
            if (_batchPages.Contains(pageIndex)) continue;
            _pageWork++;
            if (!_store.PageLocks.TryEnterUse(pageIndex))
            {
                foreach (var held in acquired)
                    _store.PageLocks.ExitUse(held);
                return false;
            }
            acquired.Add(pageIndex);
        }

        _batchPages.UnionWith(acquired);
        _reservedSlots.UnionWith(access.Slots);
        return true;
    }

    private void ReserveSlot(ItemPage page, int slot, HashSet<int> pinnedPages)
    {
        // The caller holds either the batch lease or a temporary probe lease.
        if (!_batchPages.Contains(page.PageIndex))
        {
            if (!_store.PageLocks.TryEnterUse(page.PageIndex))
                throw new InvalidOperationException("Cannot retain the current page lease.");
            _batchPages.Add(page.PageIndex);
        }
        _reservedSlots.Add((page.PageIndex << 10) | slot);
        PinForBatch(page.PageIndex, pinnedPages);
    }

    private void PinForBatch(int pageIndex, HashSet<int> pinnedPages)
    {
        if (pinnedPages.Add(pageIndex))
        {
            _pageWork++;
            _store.PinPage(pageIndex);
        }
    }

    private bool Validate(Command command, out CommandResult failure)
    {
        var access = command.Access;
        var slots = access.Slots;
        var valid = command.Kind switch
        {
            CommandKind.NopBoundary => !access.IsStrong && command.InputPayload is null,
            CommandKind.QueryType => !access.IsStrong && command.InputPayload is QueryTypePayload query && query.Type > 0,
            CommandKind.QuerySlot => access.IsStrong && command.InputPayload is QuerySlotPayload query &&
                query.Slots is not null && slots.SequenceEqual(query.Slots),
            CommandKind.Take => command.InputPayload is TakePayload take && take.Type > 0 && take.Count > 0 &&
                (take.DestinationSnapshot is null || (!take.DestinationSnapshot.IsAir && take.DestinationSnapshot.type == take.Type)),
            CommandKind.Put => command.InputPayload is PutPayload put && put.Items is not null &&
                put.Items.All(item => item is not null && !item.IsAir && item.maxStack > 0) &&
                put.Items.Distinct(ReferenceEqualityComparer.Instance).Count() == put.Items.Count,
            CommandKind.Move => access.IsStrong && command.InputPayload is MovePayload move && slots.Length == 2 &&
                slots.Contains(move.SourceSlot) && slots.Contains(move.DestinationSlot) && move.SourceSlot != move.DestinationSlot,
            CommandKind.Escape => command.InputPayload is EscapePayload { Callback: not null } &&
                (access.IsStrong || access.Weak.Type > 0),
            _ => false
        };

        if (valid && access.IsStrong)
        {
            valid = slots.Length > 0 && slots.Distinct().Count() == slots.Length &&
                slots.All(slot => slot >= 0 && PageOf(slot) < PageDirectory.MaxPageCount &&
                    _store.PageCache.Directory.Contains(PageOf(slot)));
        }

        failure = default;
        if (!valid)
        {
            failure = command.Kind == CommandKind.QuerySlot && command.InputPayload is QuerySlotPayload { Slots: not null } query
                ? CommandResult.Fail(CommandStatus.InvalidArg, query.Slots.Select(slot => new SlotInfo { SlotId = slot, IsAir = true }).ToArray())
                : Command.FailUnexecuted(command, CommandStatus.InvalidArg);
        }
        return valid;
    }

    private static void CompleteAndNotify(Command command, CommandResult result)
    {
        if (command.Kind == CommandKind.ControlTransaction && command.InputPayload is ControlTransactionPayload transaction)
            foreach (var child in transaction.Commands)
                CompleteAndNotify(child, Command.FailUnexecuted(child, result.Status));
        command.Complete(result);
        NotifyCallback(command);
    }

    private static void NotifyCallback(Command command)
    {
        try
        {
            command.Callback?.Invoke(command.Result);
        }
        catch (Exception e)
        {
            Debug.Error_Internal(e);
        }
    }

    private CommandResult RunCommandSafe(Command command, HashSet<int> touchedPages, HashSet<int> pinnedPages, bool isolateEscape = false)
    {
        CommandResult result;
        try
        {
            result = ExecuteCommand(command, touchedPages, pinnedPages, isolateEscape);
        }
        catch (Exception e)
        {
            Debug.Error_Internal(e);
            result = Command.FailUnexecuted(command, CommandStatus.InternalError);
        }

        // A weak placement that met its full request can ignore unavailable candidates.
        var fulfilled = !command.Access.IsStrong && result.IsOk &&
            (command.Kind == CommandKind.Put || command.InputPayload is TakePayload { Count: < int.MaxValue });
        if (!fulfilled && _pageBusyHit)
            result = new CommandResult(result.Status | CommandStatus.PageBusy, result.ResultPayload);
        if (!fulfilled && _pageLoadFailedHit)
            result = new CommandResult(result.Status | CommandStatus.PageLoadFailed, result.ResultPayload);
        return result;
    }

    private IEnumerable<int> EnumeratePagesWithType(int type)
    {
        if (type <= 0)
        {
            yield break;
        }

        if (_store.Config.EnableGlobalAggregate && _store.TypeIndex.TryGet(type, out _, out _, out var pages))
        {
            foreach (var page in pages)
            {
                yield return page;
            }

            yield break;
        }

        foreach (var page in _store.PageCache.AllocatedPages)
        {
            _pageWork++;
            if (!page.LoadFailed && page.Summary.Types.ContainsKey(type))
            {
                yield return page.PageIndex;
            }
        }
    }

    private CommandResult ExecuteCommand(Command command, HashSet<int> touchedPages, HashSet<int> pinnedPages, bool isolateEscape)
    {
        return command.Kind switch
        {
            CommandKind.NopBoundary => CommandResult.Ok(),
            CommandKind.QueryType => ExecuteQueryType(command),
            CommandKind.QuerySlot => ExecuteQuerySlot(command, touchedPages, pinnedPages),
            CommandKind.Take => ExecuteTake(command, pinnedPages),
            CommandKind.Put => ExecutePut(command, touchedPages, pinnedPages),
            CommandKind.Move => ExecuteMove(command, touchedPages, pinnedPages),
            CommandKind.Escape when command.Access.IsStrong && command.Access.Slots.Any(slot => ReservedQuantity(slot) != 0) =>
                CommandResult.Fail(CommandStatus.PageBusy),
            CommandKind.Escape when isolateEscape && command.InputPayload is EscapePayload { Concurrent: false, CloneItems: false } =>
                ExecuteEscape(command, touchedPages, pinnedPages, isolateItems: true),
            CommandKind.Escape when isolateEscape =>
                ExecuteConcurrentEscape(command, touchedPages, pinnedPages, forceClone: true),
            CommandKind.Escape when command.InputPayload is EscapePayload { Concurrent: true } =>
                ExecuteConcurrentEscape(command, touchedPages, pinnedPages),
            CommandKind.Escape when command.InputPayload is EscapePayload { CloneItems: true } =>
                ExecuteClonedEscape(command, touchedPages, pinnedPages),
            CommandKind.Escape => ExecuteEscape(command, touchedPages, pinnedPages),
            _ => CommandResult.Fail(CommandStatus.InvalidArg)
        };
    }

    private CommandResult ExecuteQueryType(Command command)
    {
        var payload = (QueryTypePayload)command.InputPayload;
        var type = payload.Type;
        long exactCount = 0;
        long exactFavorites = 0;
        var pages = Array.Empty<int>();

        if (_store.Config.EnableGlobalAggregate &&
            _store.TypeIndex.TryGetExact(type, out exactCount, out exactFavorites, out pages))
        {
            _pageWork += pages.Length;
        }
        else
        {
            var list = new List<int>();
            foreach (var page in _store.PageCache.AllocatedPages)
            {
                _pageWork++;
                // 不可用页的摘要只是最后已知值，不能计入可查询/可取出的物品。
                if (page.LoadFailed)
                {
                    continue;
                }

                if (page.Summary.Types.TryGetValue(type, out var stat))
                {
                    exactCount += stat.ExactCount;
                    exactFavorites += stat.ExactFavorites;
                    list.Add(page.PageIndex);
                }
            }

            pages = list.ToArray();
        }

        if (_reservedPageTypes is { Count: > 0 })
        {
            var availablePages = 0;
            foreach (var pageIndex in pages)
            {
                var reserved = _reservedPageTypes.GetValueOrDefault((pageIndex, type));
                exactCount -= reserved.Count;
                exactFavorites -= reserved.Favorites;
                if (reserved.Count == 0 || (_store.PageCache.Directory.TryGet(pageIndex, out var page) &&
                    page.Summary.Types.TryGetValue(type, out var stat) && stat.ExactCount > reserved.Count))
                    pages[availablePages++] = pageIndex;
            }
            if (availablePages != pages.Length) Array.Resize(ref pages, availablePages);
        }

        var count = (int)Math.Min(exactCount, int.MaxValue);
        var favorites = (int)Math.Min(exactFavorites, int.MaxValue);
        var saturated = exactCount > int.MaxValue || exactFavorites > int.MaxValue;

        var result = new QueryTypeResult
        {
            Type = type,
            Count = count,
            Favorites = favorites,
            Pages = pages
        };

        var status = saturated ? CommandStatus.Saturated : CommandStatus.Ok;
        return new CommandResult(status, result);
    }

    private CommandResult ExecuteQuerySlot(Command command, HashSet<int> touchedPages, HashSet<int> pinnedPages)
    {
        var payload = (QuerySlotPayload)command.InputPayload;
        if (payload.Slots is null || payload.Slots.Length == 0)
        {
            return CommandResult.Fail(CommandStatus.InvalidArg);
        }

        var found = 0;
        var result = new SlotInfo[payload.Slots.Length];
        for (var i = 0; i < payload.Slots.Length; i++)
        {
            var slotId = payload.Slots[i];

            // 页加载失败时保留该槽位的占位，并由结果标志报告失败。
            result[i] = new SlotInfo
            {
                SlotId = slotId,
                IsAir = true
            };

            if (!TryGetPageForSlot(slotId, pinnedPages, out var page))
            {
                continue;
            }

            found++;
            var item = page.Slots[SlotOf(slotId)];
            result[i].Type = item.type;
            result[i].Stack = item.stack;
            result[i].AvailableStack = item.IsAir || _store.IsEscrowSlot(slotId) ? 0 : item.stack - ReservedQuantity(slotId);
            result[i].Favorited = item.favorited;
            result[i].IsAir = item.IsAir;
        }

        return new CommandResult(found > 0 && found < result.Length ? CommandStatus.Partial : CommandStatus.Ok, result);
    }

    private CommandResult ExecuteTake(Command command, HashSet<int> pinnedPages)
    {
        var payload = (TakePayload)command.InputPayload;
        var selections = new List<TakeSelection>();
        var checkpoint = _takeReservations.Count;
        var takeAll = payload.Count == int.MaxValue;
        var remaining = payload.Count;
        var status = CommandStatus.Ok;
        try
        {
            foreach (var (page, index) in EnumerateItemSlots(command.Access, payload.Type, pinnedPages, payload.AvoidFavorited))
            {
                var item = page.Slots[index];
                if (item.IsAir || (payload.AvoidFavorited && item.favorited)) continue;
                var slotId = (page.PageIndex << 10) | index;
                var available = item.stack - ReservedQuantity(slotId);
                if (available <= 0 || _store.IsEscrowSlot(slotId)) continue;
                if (item.type != payload.Type || (payload.DestinationSnapshot is not null &&
                    !CanStackForTake(payload.DestinationSnapshot, item)))
                {
                    if (command.Access.IsStrong) status |= CommandStatus.TypeMismatch;
                    continue;
                }
                if (payload.Predicate is not null && !payload.Predicate(item, slotId, available)) continue;
                ReserveSlot(page, index, pinnedPages);
                var count = takeAll ? available : Math.Min(available, remaining);
                AddTakeReservation(command, page, index, item, count);
                selections.Add(new TakeSelection(slotId, count));
                if (!takeAll && (remaining -= count) == 0) break;
            }
        }
        catch (Exception error)
        {
            RestoreReservations(checkpoint);
            Debug.Error_Internal(error);
            return CommandResult.Fail(CommandStatus.InternalError, new TakePreview(Array.Empty<TakeSelection>()));
        }
        if ((!takeAll && remaining > 0) || selections.Count == 0) status |= CommandStatus.NotEnough;
        var fulfilled = !command.Access.IsStrong && !takeAll && remaining == 0;
        if (selections.Count > 0 && (status != CommandStatus.Ok || (!fulfilled && (_pageBusyHit || _pageLoadFailedHit))))
            status |= CommandStatus.Partial;
        return new CommandResult(status, new TakePreview(selections));
    }

    private CommandResult ExecutePut(Command command, HashSet<int> touchedPages, HashSet<int> pinnedPages)
    {
        var payload = (PutPayload)command.InputPayload;
        var inputs = payload.Items.ToArray();
        var originalSlots = new Dictionary<(ItemPage Page, int Slot), Item>();
        var changedPages = new HashSet<int>();
        var newPages = new List<ItemPage>();
        var publish = false;

        void Place(ItemPage page, int slot, Item item)
        {
            ReserveSlot(page, slot, pinnedPages);
            BeforeSlotWrite(page, slot);
            originalSlots.TryAdd((page, slot), page.Slots[slot]);
            changedPages.Add(page.PageIndex);
            page.Slots[slot] = item;
        }

        try
        {
            foreach (var input in inputs)
            {
                var source = input;
                var ownsSource = false;
                var blocked = CommandStatus.Ok;

                void MergeInto(ItemPage page, int slot)
                {
                    if (ReservedQuantity((page.PageIndex << 10) | slot) != 0 || _store.IsEscrowSlot((page.PageIndex << 10) | slot)) return;
                    var destination = page.Slots[slot];
                    if (destination.IsAir) return;
                    if (destination.type != source.type)
                    {
                        blocked |= CommandStatus.TargetOccupied | CommandStatus.TypeMismatch;
                        return;
                    }
                    if (destination.stack >= destination.maxStack)
                    {
                        blocked |= CommandStatus.TargetOccupied;
                        return;
                    }
                    if (!TryMerge(destination, source, out var nextDestination, out var nextSource, out _))
                    {
                        blocked |= CommandStatus.TargetOccupied | CommandStatus.TypeMismatch;
                        return;
                    }
                    Place(page, slot, nextDestination);
                    source = nextSource;
                    ownsSource = true;
                }

                void FillEmpty(ItemPage page, int slot)
                {
                    if (!page.Slots[slot].IsAir || _store.IsEscrowSlot((page.PageIndex << 10) | slot)) return;
                    if (source.stack <= source.maxStack)
                    {
                        Place(page, slot, source);
                        source = null;
                    }
                    else
                    {
                        if (!ownsSource)
                        {
                            source = source.Clone();
                            ownsSource = true;
                        }
                        Place(page, slot, Split(source, source.maxStack));
                    }
                }

                bool HasRemainder() => source is not null && !source.IsAir;

                void FillPage(ItemPage page)
                {
                    for (var slot = 0; slot < ItemPage.SlotCount && HasRemainder(); slot++)
                        if (page.Slots[slot].type == source.type)
                            MergeInto(page, slot);
                    for (var slot = 0; slot < ItemPage.SlotCount && HasRemainder(); slot++)
                        FillEmpty(page, slot);
                }

                if (command.Access.IsStrong)
                {
                    foreach (var slotId in command.Access.Slots)
                    {
                        if (!HasRemainder()) break;
                        if (TryGetPageForSlot(slotId, pinnedPages, out var page))
                            MergeInto(page, SlotOf(slotId));
                    }
                    foreach (var slotId in command.Access.Slots)
                    {
                        if (!HasRemainder()) break;
                        if (TryGetPageForSlot(slotId, pinnedPages, out var page))
                            FillEmpty(page, SlotOf(slotId));
                    }
                }
                else
                {
                    foreach (var page in EnumeratePlacementPages(
                        () => _store.PageCache.AllocatedPages.Select(page => page.PageIndex),
                        page => EstimatePutCapacity(page, source)))
                    {
                        FillPage(page);
                        if (!HasRemainder()) break;
                    }

                    while (HasRemainder() && payload.Spill)
                    {
                        var page = _store.AllocatePage(allowCapacityFailure: true);
                        if (page is null) break;
                        newPages.Add(page);
                        RecordNewControlPage(page);
                        if (!_store.PageLocks.TryEnterUse(page.PageIndex))
                        {
                            _pageBusyHit = true;
                            break;
                        }
                        _batchPages.Add(page.PageIndex);
                        FillPage(page);
                    }
                }

                if (HasRemainder())
                {
                    var status = CommandStatus.NoSpace;
                    if (command.Access.IsStrong)
                        status |= blocked;
                    return Command.FailUnexecuted(command, status);
                }
            }

            foreach (var pageIndex in changedPages)
            {
                if (_store.PageCache.Directory.TryGet(pageIndex, out var page))
                    MarkChanged(page, touchedPages);
            }
            publish = true;
            return CommandResult.Ok(new List<Item>());
        }
        catch (Exception e)
        {
            Debug.Error_Internal(e);
            return Command.FailUnexecuted(command, CommandStatus.InternalError);
        }
        finally
        {
            if (!publish)
            {
                foreach (var (slot, original) in originalSlots)
                    slot.Page.Slots[slot.Slot] = original;
            }

            foreach (var page in newPages)
            {
                if (publish && changedPages.Contains(page.PageIndex))
                    continue;
                ForgetNewControlPage(page);
                _store.PageCache.RemoveNewPage(page);
                pinnedPages.Remove(page.PageIndex);
                if (_batchPages.Remove(page.PageIndex))
                    _store.PageLocks.ExitUse(page.PageIndex);
            }
        }
    }

    private static long EstimatePutCapacity(ItemPage page, Item source)
    {
        // Cold pages have no stack limits in their summaries. Use type presence as a
        // merge hint and check real stack limits and mod hooks once the page is loaded.
        if (!page.Loaded)
            return (long)page.Summary.FreeSlots * source.maxStack + (page.Summary.HasType(source.type) ? 1 : 0);

        long capacity = 0;
        foreach (var item in page.Slots)
        {
            if (item.IsAir)
                capacity += source.maxStack;
            else if (item.type == source.type)
                capacity += Math.Max(0L, (long)item.maxStack - item.stack);
        }
        return capacity;
    }

    // CanStack is a compatibility query. Hooks must leave both arguments unchanged.
    private static bool CanStackForTake(Item destination, Item source) => ItemLoader.CanStack(destination, source);

    private static bool TryMerge(Item destination, Item source, out Item nextDestination, out Item nextSource, out int moved)
    {
        nextDestination = destination.Clone();
        nextSource = source.Clone();
        if (!ItemLoader.TryStackItems(nextDestination, nextSource, out moved)) return false;
        if (moved <= 0 || moved > source.stack || nextSource.stack != source.stack - moved ||
            (long)nextDestination.stack != (long)destination.stack + moved || nextDestination.stack > nextDestination.maxStack ||
            nextDestination.type != destination.type || (nextSource.stack > 0 && nextSource.type != source.type))
        {
            throw new InvalidOperationException("A stack hook changed the item type or transfer quantity.");
        }
        return true;
    }

    private static Item Split(Item remainder, int count)
    {
        var before = remainder.stack;
        var type = remainder.type;
        var split = ItemLoader.TransferWithLimit(remainder, count);
        if (split.IsAir || split.type != type || split.stack != count || remainder.type != type || remainder.stack != before - count)
        {
            throw new InvalidOperationException("A split hook changed the item type or transfer quantity.");
        }
        return split;
    }

    private CommandResult ExecuteMove(Command command, HashSet<int> touchedPages, HashSet<int> pinnedPages)
    {
        var payload = (MovePayload)command.InputPayload;
        if (!TryGetPageForSlot(payload.SourceSlot, pinnedPages, out var sourcePage) ||
            !TryGetPageForSlot(payload.DestinationSlot, pinnedPages, out var destinationPage))
        {
            return CommandResult.Fail(_pageBusyHit ? CommandStatus.PageBusy : CommandStatus.PageLoadFailed);
        }

        if (ReservedQuantity(payload.SourceSlot) != 0 || ReservedQuantity(payload.DestinationSlot) != 0)
            return CommandResult.Fail(CommandStatus.PageBusy);
        var sourceIndex = SlotOf(payload.SourceSlot);
        var destinationIndex = SlotOf(payload.DestinationSlot);
        var source = sourcePage.Slots[sourceIndex];
        if (source.IsAir) return CommandResult.Fail(CommandStatus.NotFound);

        var destination = destinationPage.Slots[destinationIndex];
        if (destination.IsAir)
        {
            BeforeSlotWrite(sourcePage, sourceIndex);
            BeforeSlotWrite(destinationPage, destinationIndex);
            sourcePage.ResetSlot(sourceIndex);
            destinationPage.Slots[destinationIndex] = source;
            MarkChanged(sourcePage, touchedPages);
            MarkChanged(destinationPage, touchedPages);
            return CommandResult.Ok(source.stack);
        }
        if (destination.type != source.type)
        {
            return CommandResult.Fail(CommandStatus.TargetOccupied | CommandStatus.TypeMismatch);
        }
        if (destination.stack >= destination.maxStack)
        {
            return CommandResult.Fail(CommandStatus.TargetOccupied);
        }
        if (!TryMerge(destination, source, out var nextDestination, out var nextSource, out var moved))
        {
            return CommandResult.Fail(CommandStatus.TargetOccupied | CommandStatus.TypeMismatch);
        }

        BeforeSlotWrite(sourcePage, sourceIndex);
        BeforeSlotWrite(destinationPage, destinationIndex);
        if (nextSource.IsAir) sourcePage.ResetSlot(sourceIndex);
        else sourcePage.Slots[sourceIndex] = nextSource;
        destinationPage.Slots[destinationIndex] = nextDestination;
        MarkChanged(sourcePage, touchedPages);
        MarkChanged(destinationPage, touchedPages);
        return new CommandResult(nextSource.IsAir ? CommandStatus.Ok : CommandStatus.Partial | CommandStatus.TargetOccupied, moved);
    }

    private CommandResult ExecuteEscape(Command command, HashSet<int> touchedPages, HashSet<int> pinnedPages, bool isolateItems = false)
    {
        var payload = (EscapePayload)command.InputPayload;
        if (payload?.Callback is null)
        {
            return CommandResult.Fail(CommandStatus.InvalidArg);
        }

        var items = new List<Item>();
        var destinations = isolateItems ? new List<(ItemPage Page, int Slot)>() : null;
        foreach (var (page, slot) in EnumerateItemSlots(command.Access, command.Access.Weak.Type, pinnedPages))
        {
            var item = page.Slots[slot];
            if (!item.IsAir)
            {
                if (ReservedQuantity((page.PageIndex << 10) | slot) != 0 || _store.IsEscrowSlot((page.PageIndex << 10) | slot))
                    return CommandResult.Fail(CommandStatus.PageBusy);
                ReserveSlot(page, slot, pinnedPages);
                if (isolateItems)
                {
                    items.Add(item.Clone());
                    destinations.Add((page, slot));
                }
                else
                {
                    items.Add(item);
                    MarkChanged(page, touchedPages);
                }
            }
        }

        try
        {
            var result = payload.Callback(items);
            if (isolateItems && !payload.ReadOnly && result.IsOk)
            {
                if (items.Count != destinations.Count)
                    return CommandResult.Fail(CommandStatus.InvalidArg | CommandStatus.EscapeError);
                var replacements = items.ConvertAll(item => item is null || item.IsAir ? new Item() : item.Clone());
                for (var i = 0; i < replacements.Count; i++)
                {
                    var (page, slot) = destinations[i];
                    BeforeSlotWrite(page, slot);
                    page.Slots[slot] = replacements[i];
                    MarkChanged(page, touchedPages);
                }
            }
            return result;
        }
        catch (Exception e)
        {
            Debug.Error_Internal(e);
            return CommandResult.Fail(CommandStatus.EscapeError | CommandStatus.InternalError);
        }
    }

    private IEnumerable<(ItemPage Page, int Slot)> EnumerateItemSlots(AccessSet access, int type,
        HashSet<int> pinnedPages, bool avoidFavorited = false)
    {
        if (access.IsStrong)
        {
            foreach (var slotId in access.Slots)
                if (TryGetPageForSlot(slotId, pinnedPages, out var page))
                    yield return (page, SlotOf(slotId));
            yield break;
        }

        long Estimate(ItemPage page)
        {
            if (!page.Summary.Types.TryGetValue(type, out var stat)) return 0;
            if (avoidFavorited && stat.ExactFavorites != 0 && page.Loaded)
            {
                long count = 0;
                for (var slot = 0; slot < page.Slots.Length; slot++)
                {
                    var item = page.Slots[slot];
                    if (!item.IsAir && item.type == type && !item.favorited)
                        count += item.stack - ReservedQuantity((page.PageIndex << 10) | slot);
                }
                return count;
            }
            var reserved = _reservedPageTypes?.GetValueOrDefault((page.PageIndex, type)) ?? default;
            return stat.ExactCount - reserved.Count - (avoidFavorited ? stat.ExactFavorites : 0);
        }

        foreach (var page in EnumeratePlacementPages(() => EnumeratePagesWithType(type), Estimate))
        {
            for (var slot = 0; slot < ItemPage.SlotCount; slot++)
            {
                var item = page.Slots[slot];
                if (!item.IsAir && item.type == type && page.Summary.SlotTypes[slot] == type &&
                    !_reservedSlots.Contains((page.PageIndex << 10) | slot))
                    yield return (page, slot);
            }
        }
    }

    private IEnumerable<ItemPage> EnumeratePlacementPages(Func<IEnumerable<int>> candidates, Func<ItemPage, long> estimate)
    {
        foreach (var page in ProbePages(OrderPages(_batchPages, estimate)))
            yield return page;

        // Candidate discovery is deferred until the retained pages cannot satisfy this request.
        foreach (var page in ProbePages(OrderPages(candidates().Where(index => !_batchPages.Contains(index)), estimate)))
            yield return page;
    }

    private int[] OrderPages(IEnumerable<int> indices, Func<ItemPage, long> estimate)
    {
        var ranked = new List<(int Index, long Capacity)>();
        foreach (var index in indices)
        {
            _pageWork++;
            if (!_store.PageCache.Directory.TryGet(index, out var page) || page.LoadFailed) continue;
            var capacity = estimate(page);
            if (capacity > 0) ranked.Add((index, capacity));
        }
        return ranked.OrderByDescending(entry => entry.Capacity).ThenBy(entry => entry.Index)
            .Select(entry => entry.Index).ToArray();
    }

    private IEnumerable<ItemPage> ProbePages(IEnumerable<int> indices)
    {
        foreach (var index in indices)
        {
            var temporary = !_batchPages.Contains(index);
            if (temporary && !_store.PageLocks.TryEnterUse(index))
            {
                _pageBusyHit = true;
                continue;
            }

            try
            {
                if (TryLoadPage(index, out var page)) yield return page;
            }
            finally
            {
                if (temporary) _store.PageLocks.ExitUse(index);
            }
        }
    }

    private bool TryGetPageForSlot(int slotId, HashSet<int> pinnedPages, out ItemPage page)
    {
        page = null;
        var index = PageOf(slotId);
        if (!_reservedSlots.Contains(slotId) || !_batchPages.Contains(index) || !TryLoadPage(index, out page))
            return false;
        PinForBatch(index, pinnedPages);
        return true;
    }

    private bool TryLoadPage(int pageIndex, out ItemPage page)
    {
        if (_store.PageCache.TryGetLoaded(pageIndex, out page))
        {
            page.LastUsedMs = PageCache.NowMs();
            return true;
        }
        try
        {
            _pageWork++;
            page = _store.EnsurePage(pageIndex);
            return true;
        }
        catch (PageBusyException)
        {
            _pageBusyHit = true;
        }
        catch (PageLoadException e)
        {
            _pageLoadFailedHit = true;
            Log.Error($"Page {pageIndex} unavailable; skipped.", e);
        }
        return false;
    }

    private void MarkChanged(ItemPage page, HashSet<int> touchedPages)
    {
        touchedPages.Add(page.PageIndex);
        _store.MarkDirty(page);
    }

    internal static int PageOf(int slotId)
    {
        return slotId >> 10;
    }

    internal static int SlotOf(int slotId)
    {
        return slotId & 0x3FF;
    }
}
