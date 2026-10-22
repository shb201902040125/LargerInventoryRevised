using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LargerInventory.Backend.Storage;
using Terraria;
using Terraria.ModLoader;

namespace LargerInventory.Backend.Commands;

/// <summary>
/// 指令执行器：一条专用线程。批次到达即执行；页加载 → 指令执行 →
/// 批次末统一同步摘要 → 解 pin → 回调（执行线程）。
/// 当前以串行为主；并行阈值字段保留，待基准校准后启用。
/// </summary>
internal sealed class CommandExecutor : IDisposable
{
    private readonly InventoryStore _store;
    private readonly CommandQueue _queue;
    private Thread _thread;
    private volatile bool _running;
    private readonly ManualResetEventSlim _paused = new(false);
    private readonly ManualResetEventSlim _resume = new(false);
    private volatile bool _pauseRequested;
    private readonly object _pageSetGate = new();

    public CommandExecutor(InventoryStore store, CommandQueue queue)
    {
        _store = store;
        _queue = queue;
    }

    public void Start()
    {
        if (_thread is not null)
        {
            return;
        }

        _running = true;
        _thread = new Thread(Loop)
        {
            IsBackground = true,
            Name = "LargerInventory.CommandExecutor"
        };
        _thread.Start();
    }

    public void Dispose()
    {
        _running = false;
        _pauseRequested = false;
        _resume.Set();
        _queue.Signal();
        _thread?.Join(1000);
        _thread = null;
    }

    /// <summary>存档屏障：在批次边界暂停执行线程。调用方负责 Resume。</summary>
    public void Pause()
    {
        if (_thread is null || Thread.CurrentThread == _thread)
        {
            // 执行线程自身（例如回调里触发存档）不能等待自己暂停。
            return;
        }

        _paused.Reset();
        _pauseRequested = true;
        _queue.Signal();
        _paused.Wait(5000);
    }

    public void Resume()
    {
        _pauseRequested = false;
        _resume.Set();
    }

    private void Loop()
    {
        while (_running)
        {
            try
            {
                if (_pauseRequested)
                {
                    _paused.Set();
                    _resume.Wait(1000);
                    _resume.Reset();
                    continue;
                }

                var now = PageCache.NowMs();
                if (_queue.TryTakeBatch(now, _store.Config.MaxBatchCommands, out var batch))
                {
                    ExecuteBatch(batch);
                    continue;
                }

                _queue.Wait(_queue.GetWaitMilliseconds(now));
            }
            catch (Exception e)
            {
                Debug.Error_Internal(e);
                Thread.Sleep(1);
            }
        }
    }

    private void ExecuteBatch(List<Command> batch)
    {
        var affectedPages = new HashSet<int>();
        var pinnedPages = new HashSet<int>();

        try
        {
            foreach (var command in batch)
            {
                PreloadPages(command, pinnedPages);
            }

            foreach (var segment in BuildSegments(batch))
            {
                var segmentAffected = new HashSet<int>();
                ExecuteSegment(segment, segmentAffected, pinnedPages);
                _store.SyncSummaries(segmentAffected);
                affectedPages.UnionWith(segmentAffected);
            }
        }
        catch (Exception e)
        {
            Debug.Error_Internal(e);
            foreach (var command in batch)
            {
                if (!command.Done.IsSet)
                {
                    command.Complete(CommandResult.Fail(CommandStatus.InternalError));
                }
            }
        }
        finally
        {
            _store.SyncSummaries(affectedPages);
            _store.PageCache.UnpinAll();
        }

        foreach (var command in batch)
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
    }

    /// <summary>
    /// 批次分段：只有"强指定且只操作已有槽位"的指令可进入并行段；
    /// 弱指定/Put/Escape 等作为屏障单独成段，保证其能看到前面所有效果（B1.1）。
    /// </summary>
    private static List<List<Command>> BuildSegments(List<Command> batch)
    {
        var segments = new List<List<Command>>();
        var runnable = new List<Command>();
        foreach (var command in batch)
        {
            if (IsParallelizable(command))
            {
                runnable.Add(command);
                continue;
            }

            if (runnable.Count > 0)
            {
                segments.Add(runnable);
                runnable = new List<Command>();
            }

            segments.Add(new List<Command> { command });
        }

        if (runnable.Count > 0)
        {
            segments.Add(runnable);
        }

        return segments;
    }

    private static bool IsParallelizable(Command command)
    {
        if (command.Access is null || !command.Access.IsStrong)
        {
            return false;
        }

        // 只并行不会分配新页、只操作已有槽位的强指定指令。
        return command.Kind is CommandKind.QuerySlot or CommandKind.Take or CommandKind.Move;
    }

    private void ExecuteSegment(List<Command> segment, HashSet<int> affectedPages, HashSet<int> pinnedPages)
    {
        if (segment.Count == 0)
        {
            return;
        }

        var components = BuildConflictComponents(segment);
        if (components.Count >= _store.Config.ParallelMinComponents &&
            EstimateSegmentUs(segment) >= _store.Config.ParallelMinCostUs &&
            components.Count > 1)
        {
            Parallel.ForEach(
                components,
                new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, _store.Config.MaxParallelism) },
                component =>
                {
                    foreach (var command in component)
                    {
                        ExecuteCommandSafe(command, affectedPages, pinnedPages);
                    }
                });
            return;
        }

        foreach (var command in segment)
        {
            ExecuteCommandSafe(command, affectedPages, pinnedPages);
        }
    }

    private void ExecuteCommandSafe(Command command, HashSet<int> affectedPages, HashSet<int> pinnedPages)
    {
        CommandResult result;
        try
        {
            result = ExecuteCommand(command, affectedPages, pinnedPages);
        }
        catch (Exception e)
        {
            Debug.Error_Internal(e);
            result = CommandResult.Fail(CommandStatus.InternalError);
        }

        command.Complete(result);
    }

    private static List<List<Command>> BuildConflictComponents(List<Command> segment)
    {
        var parent = new int[segment.Count];
        for (var i = 0; i < parent.Length; i++)
        {
            parent[i] = i;
        }

        int Find(int x)
        {
            while (parent[x] != x)
            {
                parent[x] = parent[parent[x]];
                x = parent[x];
            }

            return x;
        }

        void Union(int a, int b)
        {
            var ra = Find(a);
            var rb = Find(b);
            if (ra != rb)
            {
                parent[rb] = ra;
            }
        }

        for (var i = 0; i < segment.Count; i++)
        {
            for (var j = i + 1; j < segment.Count; j++)
            {
                if (SlotsOverlap(segment[i].Access.Slots, segment[j].Access.Slots))
                {
                    Union(i, j);
                }
            }
        }

        var groups = new Dictionary<int, List<Command>>();
        for (var i = 0; i < segment.Count; i++)
        {
            var root = Find(i);
            if (!groups.TryGetValue(root, out var list))
            {
                list = new List<Command>();
                groups[root] = list;
            }

            list.Add(segment[i]);
        }

        var order = new Dictionary<Command, int>();
        for (var i = 0; i < segment.Count; i++)
        {
            order[segment[i]] = i;
        }

        return groups.Values
            .OrderBy(list => order[list[0]])
            .ToList();
    }

    private static bool SlotsOverlap(int[] a, int[] b)
    {
        if (a is null || b is null || a.Length == 0 || b.Length == 0)
        {
            return false;
        }

        foreach (var x in a)
        {
            foreach (var y in b)
            {
                if (x == y)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static int EstimateSegmentUs(List<Command> segment)
    {
        var total = 0;
        foreach (var command in segment)
        {
            total += 100 + (command.Access?.Slots?.Length ?? 0) * 20;
        }

        return total;
    }

    private void PreloadPages(Command command, HashSet<int> pinnedPages)
    {
        foreach (var pageIndex in EnumerateRequiredPages(command))
        {
            if (!pinnedPages.Add(pageIndex))
            {
                continue;
            }

            _store.EnsurePage(pageIndex);
            _store.PinPage(pageIndex);
        }
    }

    private IEnumerable<int> EnumerateRequiredPages(Command command)
    {
        switch (command.Kind)
        {
            case CommandKind.QuerySlot:
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
                if (command.Payload is PutPayload put)
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
        return command.Payload switch
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
            if (page.Summary.Types.ContainsKey(type))
            {
                yield return page.PageIndex;
            }
        }
    }

    private CommandResult ExecuteCommand(Command command, HashSet<int> affectedPages, HashSet<int> pinnedPages)
    {
        return command.Kind switch
        {
            CommandKind.NopBoundary => CommandResult.Ok(),
            CommandKind.QueryType => ExecuteQueryType(command),
            CommandKind.QuerySlot => ExecuteQuerySlot(command, affectedPages, pinnedPages),
            CommandKind.Take => ExecuteTake(command, affectedPages, pinnedPages),
            CommandKind.Put => ExecutePut(command, affectedPages, pinnedPages),
            CommandKind.Move => ExecuteMove(command, affectedPages, pinnedPages),
            CommandKind.Escape => ExecuteEscape(command, affectedPages, pinnedPages),
            _ => CommandResult.Fail(CommandStatus.InvalidArg)
        };
    }

    private CommandResult ExecuteQueryType(Command command)
    {
        var payload = (QueryTypePayload)command.Payload;
        var type = payload.Type;
        var count = 0;
        var favorites = 0;
        var pages = Array.Empty<int>();

        if (_store.Config.EnableGlobalAggregate && _store.TypeIndex.TryGet(type, out count, out favorites, out pages))
        {
        }
        else
        {
            var list = new List<int>();
            foreach (var page in _store.PageCache.AllocatedPages)
            {
                if (page.Summary.Types.TryGetValue(type, out var stat))
                {
                    count = TypeStat.SatAdd(count, stat.Count);
                    favorites = TypeStat.SatAdd(favorites, stat.Favorites);
                    list.Add(page.PageIndex);
                }
            }

            pages = list.ToArray();
        }

        var result = new QueryTypeResult
        {
            Type = type,
            Count = count,
            Favorites = favorites,
            Pages = pages
        };

        // int 饱和后总量不精确：用 SATURATED 明示。
        var status = count == int.MaxValue ? CommandStatus.Saturated : CommandStatus.Ok;
        return new CommandResult(status, result);
    }

    private CommandResult ExecuteQuerySlot(Command command, HashSet<int> affectedPages, HashSet<int> pinnedPages)
    {
        var payload = (QuerySlotPayload)command.Payload;
        if (payload.Slots is null || payload.Slots.Length == 0)
        {
            return CommandResult.Fail(CommandStatus.InvalidArg);
        }

        var status = CommandStatus.Ok;
        var result = new SlotInfo[payload.Slots.Length];
        for (var i = 0; i < payload.Slots.Length; i++)
        {
            var slotId = payload.Slots[i];
            if (!TryGetPageForSlot(slotId, affectedPages, pinnedPages, out var page))
            {
                status |= CommandStatus.InvalidArg;
                continue;
            }

            var item = page.Slots[SlotOf(slotId)];
            result[i] = new SlotInfo
            {
                SlotId = slotId,
                Type = item.type,
                Stack = item.stack,
                Favorited = item.favorited,
                IsAir = item.IsAir
            };
        }

        return new CommandResult(status, result);
    }

    private CommandResult ExecuteTake(Command command, HashSet<int> affectedPages, HashSet<int> pinnedPages)
    {
        var payload = (TakePayload)command.Payload;
        if (payload.Count <= 0)
        {
            return CommandResult.Fail(CommandStatus.InvalidArg);
        }

        var results = new List<Item>();
        var requestAll = payload.Count == int.MaxValue;
        var remaining = requestAll ? int.MaxValue : payload.Count;
        var typeMismatch = false;
        var saturated = false;
        if (_store.Config.EnableGlobalAggregate &&
            _store.TypeIndex.TryGet(payload.Type, out var totalBefore, out _, out _))
        {
            saturated = totalBefore == int.MaxValue;
        }

        foreach (var slotId in EnumerateCandidateSlots(command.Access, payload.Type))
        {
            if (remaining <= 0)
            {
                break;
            }

            if (!TryGetPageForSlot(slotId, affectedPages, pinnedPages, out var page))
            {
                continue;
            }

            var index = SlotOf(slotId);
            var item = page.Slots[index];
            if (item.IsAir)
            {
                continue;
            }

            if (item.type != payload.Type)
            {
                typeMismatch = true;
                continue;
            }

            if (payload.AvoidFavorited && item.favorited)
            {
                continue;
            }

            var take = Math.Min(item.stack, remaining);
            if (take <= 0)
            {
                continue;
            }

            if (take == item.stack)
            {
                results.Add(item);
                page.ResetSlot(index);
            }
            else
            {
                var clone = item.Clone();
                clone.stack = take;
                item.stack -= take;
                results.Add(clone);
            }

            remaining -= take;
            MarkChanged(page, affectedPages);
        }

        var status = CommandStatus.Ok;
        if (saturated)
        {
            status |= CommandStatus.Saturated;
        }

        if (results.Count == 0)
        {
            status |= CommandStatus.NotEnough;
            if (typeMismatch)
            {
                status |= CommandStatus.TypeMismatch;
            }
        }
        else if (remaining > 0 || requestAll)
        {
            // count=int.MaxValue 是"尽可能取完"哨兵：未达到哨兵即视为部分完成。
            status |= CommandStatus.Partial;
            if (remaining > 0 && !requestAll)
            {
                status |= CommandStatus.NotEnough;
            }
        }

        return new CommandResult(status, results);
    }

    private CommandResult ExecutePut(Command command, HashSet<int> affectedPages, HashSet<int> pinnedPages)
    {
        var payload = (PutPayload)command.Payload;
        if (payload.Items is null)
        {
            return CommandResult.Fail(CommandStatus.InvalidArg);
        }

        var remainingItems = new List<Item>();
        foreach (var item in payload.Items.ToArray())
        {
            if (item is null || item.IsAir || item.stack <= 0)
            {
                continue;
            }

            var left = item.stack;

            // 1) 合并到已有同类型堆
            foreach (var slotId in EnumerateMergeCandidates(command.Access, item.type))
            {
                if (left <= 0)
                {
                    break;
                }

                if (!TryGetPageForSlot(slotId, affectedPages, pinnedPages, out var page))
                {
                    continue;
                }

                var index = SlotOf(slotId);
                var destination = page.Slots[index];
                if (destination.IsAir || destination.type != item.type || !ItemLoader.CanStack(destination, item))
                {
                    continue;
                }

                var move = Math.Min(destination.maxStack - destination.stack, left);
                if (move <= 0)
                {
                    continue;
                }

                ItemLoader.OnStack(destination, item, move);
                destination.stack += move;
                left -= move;
                item.stack = left;
                MarkChanged(page, affectedPages);
            }

            // 2) 放入空槽（或按 spill 策略新开页）
            var placed = false;
            if (left > 0)
            {
                foreach (var slotId in EnumerateEmptySlots(command.Access, payload.Spill, affectedPages, pinnedPages))
                {
                    if (left <= 0)
                    {
                        break;
                    }

                    if (!TryGetPageForSlot(slotId, affectedPages, pinnedPages, out var page))
                    {
                        continue;
                    }

                    var index = SlotOf(slotId);
                    if (!page.Slots[index].IsAir)
                    {
                        continue;
                    }

                    if (left <= item.maxStack)
                    {
                        page.Slots[index] = item;
                        item.stack = left;
                        placed = true;
                        MarkChanged(page, affectedPages);
                        break;
                    }

                    var clone = item.Clone();
                    clone.stack = item.maxStack;
                    page.Slots[index] = clone;
                    left -= item.maxStack;
                    item.stack = left;
                    MarkChanged(page, affectedPages);
                }
            }

            if (!placed && left > 0)
            {
                item.stack = left;
                remainingItems.Add(item);
            }
        }

        var status = remainingItems.Count == 0
            ? CommandStatus.Ok
            : CommandStatus.Partial | CommandStatus.NoSpace;
        return new CommandResult(status, remainingItems);
    }

    private CommandResult ExecuteMove(Command command, HashSet<int> affectedPages, HashSet<int> pinnedPages)
    {
        var payload = (MovePayload)command.Payload;
        if (payload.SourceSlot < 0 || payload.DestinationSlot < 0)
        {
            return CommandResult.Fail(CommandStatus.InvalidArg);
        }

        if (!TryGetPageForSlot(payload.SourceSlot, affectedPages, pinnedPages, out var sourcePage) ||
            !TryGetPageForSlot(payload.DestinationSlot, affectedPages, pinnedPages, out var destinationPage))
        {
            return CommandResult.Fail(CommandStatus.InvalidArg);
        }

        var sourceIndex = SlotOf(payload.SourceSlot);
        var destinationIndex = SlotOf(payload.DestinationSlot);
        var source = sourcePage.Slots[sourceIndex];
        if (source.IsAir)
        {
            return CommandResult.Fail(CommandStatus.NotFound);
        }

        var destination = destinationPage.Slots[destinationIndex];
        if (destination.IsAir)
        {
            destinationPage.Slots[destinationIndex] = source;
            sourcePage.ResetSlot(sourceIndex);
            MarkChanged(sourcePage, affectedPages);
            MarkChanged(destinationPage, affectedPages);
            return CommandResult.Ok();
        }

        if (!ItemLoader.CanStack(destination, source))
        {
            return CommandResult.Fail(CommandStatus.TargetOccupied | CommandStatus.TypeMismatch);
        }

        var move = Math.Min(destination.maxStack - destination.stack, source.stack);
        if (move <= 0)
        {
            return CommandResult.Fail(CommandStatus.TargetOccupied);
        }

        ItemLoader.OnStack(destination, source, move);
        destination.stack += move;
        source.stack -= move;
        if (source.stack <= 0)
        {
            sourcePage.ResetSlot(sourceIndex);
        }

        MarkChanged(sourcePage, affectedPages);
        MarkChanged(destinationPage, affectedPages);
        return CommandResult.Ok(move);
    }

    private CommandResult ExecuteEscape(Command command, HashSet<int> affectedPages, HashSet<int> pinnedPages)
    {
        var payload = (EscapePayload)command.Payload;
        if (payload?.Callback is null)
        {
            return CommandResult.Fail(CommandStatus.InvalidArg);
        }

        var items = new List<Item>();
        foreach (var slotId in EnumerateCandidateSlots(command.Access, GetTakeType(command)))
        {
            if (!TryGetPageForSlot(slotId, affectedPages, pinnedPages, out var page))
            {
                continue;
            }

            var item = page.Slots[SlotOf(slotId)];
            if (!item.IsAir)
            {
                items.Add(item);
                MarkChanged(page, affectedPages);
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

    private IEnumerable<int> EnumerateMergeCandidates(AccessSet access, int type)
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

        foreach (var pageIndex in EnumeratePagesWithType(type))
        {
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

    private IEnumerable<int> EnumerateEmptySlots(AccessSet access, bool spill, HashSet<int> affectedPages, HashSet<int> pinnedPages)
    {
        if (access.IsStrong)
        {
            foreach (var slot in access.Slots)
            {
                if (slot < 0)
                {
                    continue;
                }

                if (TryGetPageForSlot(slot, affectedPages, pinnedPages, out var page) && page.Slots[SlotOf(slot)].IsAir)
                {
                    yield return slot;
                }
            }

            yield break;
        }

        foreach (var page in _store.PageCache.AllocatedPages.ToArray())
        {
            if (page.Summary.FreeSlots <= 0)
            {
                continue;
            }

            if (!TryGetPageForSlot((page.PageIndex << 10), affectedPages, pinnedPages, out var loaded))
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

        var newPage = _store.AllocatePage();
        pinnedPages.Add(newPage.PageIndex);
        _store.PinPage(newPage.PageIndex);
        affectedPages.Add(newPage.PageIndex);
        for (var i = 0; i < ItemPage.SlotCount; i++)
        {
            yield return (newPage.PageIndex << 10) | i;
        }
    }

    private bool TryGetPageForSlot(int slotId, HashSet<int> affectedPages, HashSet<int> pinnedPages, out ItemPage page)
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

        lock (_pageSetGate)
        {
            if (pinnedPages.Add(pageIndex))
            {
                _store.EnsurePage(pageIndex);
                _store.PinPage(pageIndex);
            }

            page = _store.EnsurePage(pageIndex);
        }

        return true;
    }

    private void MarkChanged(ItemPage page, HashSet<int> affectedPages)
    {
        lock (_pageSetGate)
        {
            affectedPages.Add(page.PageIndex);
        }

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



