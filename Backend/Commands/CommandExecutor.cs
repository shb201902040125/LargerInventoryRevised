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
/// 指令执行器（无后台线程）。所有 Item、ItemLoader、ItemIO 及模组 Hook 调用都发生在调用
/// <see cref="Pump"/> / <see cref="Drain"/> 的线程上，即 Terraria 主线程；指令回调也在同一线程执行。
/// 执行与存档共用 <see cref="_executeGate"/>，保证存档不会与执行交错。
/// </summary>
internal sealed class CommandExecutor
{
    private const int MaxDrainBatches = 1024;

    private readonly InventoryStore _store;
    private readonly CommandQueue _queue;
    private readonly object _executeGate = new();
    private volatile int _executingThreadId;
    private HashSet<int> _activeLocks;
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
                executed += batch.Count;
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

            var budget = MaxDrainBatches;
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
        if (_executingThreadId == Environment.CurrentManagedThreadId)
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

    internal CommandResult ExecuteTransactionCommand(Command command)
    {
        if (!Monitor.IsEntered(_executeGate) || _executingThreadId != 0)
        {
            return Command.FailUnexecuted(command, CommandStatus.PageBusy);
        }

        if (!command.TryMarkSubmitted())
        {
            return CommandResult.Fail(CommandStatus.Cancelled);
        }

        _executingThreadId = Environment.CurrentManagedThreadId;
        try
        {
            ExecuteBatch(new List<Command> { command }, notify: false);
            return command.Result;
        }
        finally
        {
            _executingThreadId = 0;
        }
    }

    private void ExecuteBatch(List<Command> batch, bool notify = true)
    {
        var pinnedPages = new HashSet<int>();
        var touchedPages = new HashSet<int>();
        var index = 0;

        try
        {
            for (; index < batch.Count; index++)
            {
                if (Integration.InventorySystem.CommandsPaused)
                {
                    for (; index < batch.Count; index++)
                    {
                        if (!batch[index].Done.IsSet)
                        {
                            batch[index].Complete(Command.FailUnexecuted(batch[index], CommandStatus.ManualInterrupt));
                        }
                    }

                    break;
                }

                var command = batch[index];
                if (!Validate(command, out var validationResult))
                {
                    command.Complete(validationResult);
                    continue;
                }

                var locks = new HashSet<int>();
                if (!TryLockCommandPages(command, locks))
                {
                    // 修复服务正在独占该页：本次指令不执行，按 PageBusy 失败。
                    command.Complete(Command.FailUnexecuted(command, CommandStatus.PageBusy));
                    continue;
                }

                _activeLocks = locks;
                _pageBusyHit = false;
                _pageLoadFailedHit = false;
                try
                {
                    ExecuteCommandSafe(command, touchedPages, pinnedPages);
                    _store.SyncSummaries(touchedPages);
                    touchedPages.Clear();
                }
                finally
                {
                    _activeLocks = null;
                    foreach (var pageIndex in locks)
                    {
                        _store.PageLocks.Exit(pageIndex);
                    }
                }
            }
        }
        catch (Exception e)
        {
            Debug.Error_Internal(e);
            for (var i = index; i < batch.Count; i++)
            {
                if (!batch[i].Done.IsSet)
                {
                    batch[i].Complete(Command.FailUnexecuted(batch[i], CommandStatus.InternalError));
                }
            }
        }
        finally
        {
            _store.SyncSummaries(touchedPages);
            _store.PageCache.UnpinAll();
            _store.CaptureDirtyPageSnapshots();
        }

        if (!notify) return;

        foreach (var command in batch)
        {
            NotifyCallback(command);
        }
    }

    /// <summary>按页索引升序获取该指令需要的所有页锁；任一失败则释放并返回 false。</summary>
    private bool TryLockCommandPages(Command command, HashSet<int> locks)
    {
        var pages = new SortedSet<int>();
        foreach (var pageIndex in EnumerateLockedPages(command))
        {
            if (pageIndex >= 0)
            {
                pages.Add(pageIndex);
            }
        }

        foreach (var pageIndex in pages)
        {
            _pageWork++;

            if (!_store.PageLocks.TryEnter(pageIndex))
            {
                // 强指定指令必须有这些页；弱指定指令可以跳过忙碌页，继续使用其它页。
                if (command.Access.IsStrong)
                {
                    foreach (var held in locks)
                    {
                        _store.PageLocks.Exit(held);
                    }

                    return false;
                }

                continue;
            }

            locks.Add(pageIndex);
        }

        return true;
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

    private IEnumerable<int> EnumerateLockedPages(Command command)
    {
        if (command.Access.IsStrong)
        {
            foreach (var slot in command.Access.Slots)
            {
                if (slot >= 0)
                {
                    yield return PageOf(slot);
                }
            }

            yield break;
        }

        foreach (var pageIndex in EnumerateRequiredPages(command))
        {
            yield return pageIndex;
        }
    }

    /// <summary>执行期动态遇到的页（例如 Put spill 新页）也必须立即加锁，避免中途被修复服务锁定。</summary>
    private bool EnsureActivePageLock(int pageIndex)
    {
        if (_activeLocks is null)
        {
            return false;
        }

        if (_activeLocks.Contains(pageIndex))
        {
            return true;
        }

        if (!_store.PageLocks.TryEnter(pageIndex))
        {
            _pageBusyHit = true;
            return false;
        }

        _activeLocks.Add(pageIndex);
        return true;
    }

    private static void CompleteAndNotify(Command command, CommandResult result)
    {
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

    private void ExecuteCommandSafe(Command command, HashSet<int> touchedPages, HashSet<int> pinnedPages)
    {
        CommandResult result;
        try
        {
            result = ExecuteCommand(command, touchedPages, pinnedPages);
        }
        catch (Exception e)
        {
            Debug.Error_Internal(e);
            result = Command.FailUnexecuted(command, CommandStatus.InternalError);
        }

        if (_pageBusyHit)
        {
            result = new CommandResult(result.Status | CommandStatus.PageBusy, result.ResultPayload);
        }

        if (_pageLoadFailedHit) result = new CommandResult(result.Status | CommandStatus.PageLoadFailed, result.ResultPayload);
        command.Complete(result);
    }

    private IEnumerable<int> EnumerateRequiredPages(Command command)
    {
        switch (command.Kind)
        {
            // QuerySlot 只读，按需加载即可；预加载会让一次查询对同一坏页记录两次失败日志。
            case CommandKind.Move:
            case CommandKind.Escape when command.Access.IsStrong:
                if (command.Access.IsStrong)
                {
                    foreach (var slot in command.Access.Slots)
                    {
                        if (slot >= 0)
                        {
                            yield return PageOf(slot);
                        }
                    }
                }

                break;

            case CommandKind.Take:
                foreach (var pageIndex in EnumeratePagesWithType(GetTakeType(command)))
                {
                    yield return pageIndex;
                }

                break;

            case CommandKind.Escape:
                foreach (var pageIndex in EnumeratePagesWithType(GetTakeType(command)))
                {
                    yield return pageIndex;
                }

                break;

            case CommandKind.Put:
                if (command.InputPayload is PutPayload put)
                {
                    if (command.Access.IsStrong)
                    {
                        foreach (var slot in command.Access.Slots)
                        {
                            if (slot >= 0)
                            {
                                yield return PageOf(slot);
                            }
                        }
                    }
                    else
                    {
                        if (put.Items is not null)
                        {
                            foreach (var item in put.Items)
                            {
                                if (item is null || item.IsAir)
                                {
                                    continue;
                                }

                                foreach (var pageIndex in EnumeratePagesWithType(item.type))
                                {
                                    yield return pageIndex;
                                }
                            }
                        }

                        foreach (var page in _store.PageCache.AllocatedPages)
                        {
                            _pageWork++;
                            if (page.Summary.FreeSlots > 0)
                            {
                                yield return page.PageIndex;
                            }
                        }
                    }
                }

                break;
        }
    }

    private static int GetTakeType(Command command)
    {
        return command.InputPayload switch
        {
            TakePayload take => take.Type,
            _ => command.Access.Weak.Type
        };
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

    private CommandResult ExecuteCommand(Command command, HashSet<int> touchedPages, HashSet<int> pinnedPages)
    {
        return command.Kind switch
        {
            CommandKind.NopBoundary => CommandResult.Ok(),
            CommandKind.QueryType => ExecuteQueryType(command),
            CommandKind.QuerySlot => ExecuteQuerySlot(command, touchedPages, pinnedPages),
            CommandKind.Take => ExecuteTake(command, touchedPages, pinnedPages),
            CommandKind.Put => ExecutePut(command, touchedPages, pinnedPages),
            CommandKind.Move => ExecuteMove(command, touchedPages, pinnedPages),
            CommandKind.Escape => ExecuteEscape(command, touchedPages, pinnedPages),
            _ => CommandResult.Fail(CommandStatus.InvalidArg)
        };
    }

    private CommandResult ExecuteQueryType(Command command)
    {
        var payload = (QueryTypePayload)command.InputPayload;
        var type = payload.Type;
        var count = 0;
        var favorites = 0;
        var saturated = false;
        var pages = Array.Empty<int>();

        if (_store.Config.EnableGlobalAggregate && _store.TypeIndex.TryGet(type, out count, out favorites, out pages, out saturated))
        {
            _pageWork += pages.Length;
        }
        else
        {
            long exactCount = 0;
            long exactFavorites = 0;
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

            count = (int)Math.Min(exactCount, int.MaxValue);
            favorites = (int)Math.Min(exactFavorites, int.MaxValue);
            saturated = exactCount > int.MaxValue || exactFavorites > int.MaxValue;
            pages = list.ToArray();
        }

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
            result[i].Favorited = item.favorited;
            result[i].IsAir = item.IsAir;
        }

        return new CommandResult(found > 0 && found < result.Length ? CommandStatus.Partial : CommandStatus.Ok, result);
    }

    private CommandResult ExecuteTake(Command command, HashSet<int> touchedPages, HashSet<int> pinnedPages)
    {
        var payload = (TakePayload)command.InputPayload;
        var results = new List<Item>();
        var takeAll = payload.Count == int.MaxValue;
        var remaining = payload.Count;
        var status = CommandStatus.Ok;
        try
        {
            foreach (var slotId in EnumerateCandidateSlots(command.Access, payload.Type))
            {
                if (!takeAll && remaining == 0)
                {
                    break;
                }

                if (!TryGetPageForSlot(slotId, pinnedPages, out var page))
                {
                    continue;
                }

                var index = SlotOf(slotId);
                var item = page.Slots[index];
                if (item.IsAir || (payload.AvoidFavorited && item.favorited))
                {
                    continue;
                }

                if (item.type != payload.Type || (payload.DestinationSnapshot is not null &&
                    !ItemLoader.CanStack(payload.DestinationSnapshot.Clone(), item.Clone())))
                {
                    if (command.Access.IsStrong) status |= CommandStatus.TypeMismatch;
                    continue;
                }

                var count = takeAll ? item.stack : Math.Min(item.stack, remaining);
                if (count == item.stack)
                {
                    page.ResetSlot(index);
                    results.Add(item);
                }
                else
                {
                    var remainder = item.Clone();
                    var taken = Split(remainder, count);
                    page.Slots[index] = remainder;
                    results.Add(taken);
                }

                if (!takeAll) remaining -= count;
                MarkChanged(page, touchedPages);
            }
        }
        catch (Exception e)
        {
            Debug.Error_Internal(e);
            status |= CommandStatus.InternalError;
        }

        if ((!takeAll && remaining > 0) || results.Count == 0)
        {
            status |= CommandStatus.NotEnough;
        }
        if (results.Count > 0 && (status != CommandStatus.Ok || _pageBusyHit || _pageLoadFailedHit))
        {
            status |= CommandStatus.Partial;
        }
        return new CommandResult(status, results);
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
            originalSlots.TryAdd((page, slot), page.Slots[slot]);
            changedPages.Add(page.PageIndex);
            page.Slots[slot] = item;
        }

        try
        {
            foreach (var input in inputs)
            {
                var source = input;
                var blocked = CommandStatus.Ok;
                foreach (var slotId in EnumerateMergeCandidates(command.Access, source.type, changedPages))
                {
                    if (source.IsAir) break;
                    if (!TryGetPageForSlot(slotId, pinnedPages, out var page)) continue;

                    var slot = SlotOf(slotId);
                    var destination = page.Slots[slot];
                    if (destination.IsAir) continue;
                    if (source.type != destination.type)
                    {
                        blocked |= CommandStatus.TargetOccupied | CommandStatus.TypeMismatch;
                        continue;
                    }
                    if (destination.stack >= destination.maxStack)
                    {
                        blocked |= CommandStatus.TargetOccupied;
                        continue;
                    }
                    if (!TryMerge(destination, source, out var nextDestination, out var nextSource, out _))
                    {
                        blocked |= CommandStatus.TargetOccupied | CommandStatus.TypeMismatch;
                        continue;
                    }

                    Place(page, slot, nextDestination);
                    source = nextSource;
                }

                // Avoid advancing the empty-slot iterator after a complete transfer, which could allocate a new page.
                while (!source.IsAir)
                {
                    using var emptySlots = EnumerateEmptySlots(command.Access, payload.Spill, pinnedPages, newPages).GetEnumerator();
                    var placed = false;
                    while (!source.IsAir && emptySlots.MoveNext())
                    {
                        var slotId = emptySlots.Current;
                        if (!TryGetPageForSlot(slotId, pinnedPages, out var page)) continue;

                        var slot = SlotOf(slotId);
                        if (!page.Slots[slot].IsAir) continue;
                        if (source.stack <= source.maxStack)
                        {
                            Place(page, slot, source);
                            source = null;
                        }
                        else
                        {
                            var nextSource = source.Clone();
                            Place(page, slot, Split(nextSource, source.maxStack));
                            source = nextSource;
                        }
                        placed = true;
                        if (source is null) break;
                    }
                    if (source is null || !placed || command.Access.IsStrong || !payload.Spill) break;
                }

                if (source is not null && !source.IsAir)
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
                _store.PageCache.RemoveNewPage(page);
                pinnedPages.Remove(page.PageIndex);
            }
        }
    }

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

        var sourceIndex = SlotOf(payload.SourceSlot);
        var destinationIndex = SlotOf(payload.DestinationSlot);
        var source = sourcePage.Slots[sourceIndex];
        if (source.IsAir) return CommandResult.Fail(CommandStatus.NotFound);

        var destination = destinationPage.Slots[destinationIndex];
        if (destination.IsAir)
        {
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

        if (nextSource.IsAir) sourcePage.ResetSlot(sourceIndex);
        else sourcePage.Slots[sourceIndex] = nextSource;
        destinationPage.Slots[destinationIndex] = nextDestination;
        MarkChanged(sourcePage, touchedPages);
        MarkChanged(destinationPage, touchedPages);
        return new CommandResult(nextSource.IsAir ? CommandStatus.Ok : CommandStatus.Partial | CommandStatus.TargetOccupied, moved);
    }

    private CommandResult ExecuteEscape(Command command, HashSet<int> touchedPages, HashSet<int> pinnedPages)
    {
        var payload = (EscapePayload)command.InputPayload;
        if (payload?.Callback is null)
        {
            return CommandResult.Fail(CommandStatus.InvalidArg);
        }

        var items = new List<Item>();
        foreach (var slotId in EnumerateCandidateSlots(command.Access, GetTakeType(command)))
        {
            if (!TryGetPageForSlot(slotId, pinnedPages, out var page))
            {
                continue;
            }

            var item = page.Slots[SlotOf(slotId)];
            if (!item.IsAir)
            {
                items.Add(item);
                MarkChanged(page, touchedPages);
            }
        }

        try
        {
            return payload.Callback(items);
        }
        catch (Exception e)
        {
            Debug.Error_Internal(e);
            return CommandResult.Fail(CommandStatus.EscapeError | CommandStatus.InternalError);
        }
    }

    private IEnumerable<int> EnumerateCandidateSlots(AccessSet access, int type)
    {
        if (access.IsStrong)
        {
            foreach (var slot in access.Slots)
            {
                if (slot >= 0)
                {
                    yield return slot;
                }
            }

            yield break;
        }

        if (type <= 0)
        {
            yield break;
        }

        foreach (var pageIndex in EnumeratePagesWithType(type))
        {
            _pageWork++;
            if (!_store.PageCache.Directory.TryGet(pageIndex, out var page))
            {
                continue;
            }

            for (var i = 0; i < ItemPage.SlotCount; i++)
            {
                if (page.Summary.SlotTypes[i] == type)
                {
                    yield return (pageIndex << 10) | i;
                }
            }
        }
    }

    private IEnumerable<int> EnumerateMergeCandidates(AccessSet access, int type, HashSet<int> touchedPages)
    {
        if (access.IsStrong)
        {
            foreach (var slot in access.Slots)
            {
                yield return slot;
            }
            yield break;
        }

        var modifiedPages = touchedPages.ToHashSet();
        foreach (var slot in EnumerateCandidateSlots(access, type))
        {
            if (!modifiedPages.Contains(PageOf(slot))) yield return slot;
        }
        // Slots filled by earlier input items need to participate before the command's summary is rebuilt.
        foreach (var pageIndex in modifiedPages)
        {
            _pageWork++;
            if (!_store.PageCache.TryGetLoaded(pageIndex, out var page)) continue;
            for (var index = 0; index < ItemPage.SlotCount; index++)
            {
                if (!page.Slots[index].IsAir && page.Slots[index].type == type)
                {
                    yield return (pageIndex << 10) | index;
                }
            }
        }
    }

    private IEnumerable<int> EnumerateEmptySlots(AccessSet access, bool spill, HashSet<int> pinnedPages, List<ItemPage> newPages)
    {
        if (access.IsStrong)
        {
            foreach (var slot in access.Slots)
            {
                if (slot < 0)
                {
                    continue;
                }

                if (TryGetPageForSlot(slot, pinnedPages, out var page) && page.Slots[SlotOf(slot)].IsAir)
                {
                    yield return slot;
                }
            }

            yield break;
        }

        foreach (var page in _store.PageCache.AllocatedPages.ToArray())
        {
            _pageWork++;
            if (page.LoadFailed || page.Summary.FreeSlots <= 0)
            {
                continue;
            }

            if (!TryGetPageForSlot(page.PageIndex << 10, pinnedPages, out var loaded))
            {
                continue;
            }

            for (var i = 0; i < ItemPage.SlotCount; i++)
            {
                if (loaded.Slots[i].IsAir)
                {
                    yield return (loaded.PageIndex << 10) | i;
                }
            }
        }

        if (!spill)
        {
            yield break;
        }

        var newPage = _store.AllocatePage(allowCapacityFailure: true);
        if (newPage is null)
            yield break;

        _pageWork++;
        newPages.Add(newPage);
        if (!EnsureActivePageLock(newPage.PageIndex))
        {
            yield break;
        }

        pinnedPages.Add(newPage.PageIndex);
        _store.PinPage(newPage.PageIndex);
        for (var i = 0; i < ItemPage.SlotCount; i++)
        {
            yield return (newPage.PageIndex << 10) | i;
        }
    }

    /// <summary>
    /// 获取槽位所在页。页无法加载（文件损坏、缺失等）时返回 false，调用方跳过该槽位，
    /// 从而保证不会向不可读的页写入，也不会在命令中途抛出导致部分提交。
    /// </summary>
    private bool TryGetPageForSlot(int slotId, HashSet<int> pinnedPages, out ItemPage page)
    {
        page = null;
        if (slotId < 0)
        {
            return false;
        }

        var pageIndex = PageOf(slotId);
        if (pageIndex < 0 || pageIndex >= PageDirectory.MaxPageCount)
        {
            return false;
        }

        // 即使最终因锁忙碌而失败，也记录“该玩家请求过此页”，供修复完成后的变更通知使用。

        // 页不存在时视为非法槽位，不能为了让查询成功而分配一个空页并写入 store.meta。
        if (!_store.PageCache.Directory.Contains(pageIndex))
        {
            return false;
        }

        if (!EnsureActivePageLock(pageIndex))
        {
            return false;
        }

        try
        {
            page = _store.EnsurePage(pageIndex);
        }
        catch (PageLoadException e)
        {
            _pageLoadFailedHit = true;
            Log.Error($"Page {pageIndex} unavailable; slot {slotId} skipped.", e);
            return false;
        }

        if (pinnedPages.Add(pageIndex))
        {
            _pageWork++;
            _store.PinPage(pageIndex);
        }

        return true;
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
