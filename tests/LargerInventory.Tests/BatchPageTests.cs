using System.Collections.Generic;
using System.Linq;
using LargerInventory.Backend;
using LargerInventory.Backend.Commands;
using LargerInventory.Backend.Storage;
using Terraria;

namespace LargerInventory.Tests;

internal static partial class Program
{
    private const int BatchDeferred = int.MaxValue;

    private static void RunBatchPageTests()
    {
        Run(nameof(Batch_SharedPageUseExcludesRepair));
        Run(nameof(Batch_StrongSamePageCommandsRunInFifoOrder));
        Run(nameof(Batch_WeakTakeUsesHeldPageBeforeExpanding));
        Run(nameof(Batch_WeakPutUsesHeldPageBeforeExpanding));
        Run(nameof(Batch_WeakTakePrefersUsableCapacityAndStableTies));
        Run(nameof(Batch_WeakPutPrefersFreeSlotsAndStableTies));
        Run(nameof(Batch_FailedPutLeavesSlotAvailableForNextCommand));
        Run(nameof(Batch_WeakTakeSkipsRepairPageAndCompletes));
    }

    private static InventoryStore CreateBatchTestStore(bool enableAggregate = true) => new(new StoreConfig
    {
        MaxBatchesPerPump = 1,
        EnableGlobalAggregate = enableAggregate
    });

    private static int BatchSlot(ItemPage page, int index = 0) => (page.PageIndex << 10) | index;

    // QuerySlot's factory creates a boundary, so construct a deferred query for these same-batch fixtures.
    private static Command DeferredBatchQuery(params int[] slots) => new(CommandKind.QuerySlot, BatchDeferred,
        AccessSet.Strong(slots), new QuerySlotPayload { Slots = slots });

    private static void PumpBatch(InventoryStore store, params Command[] commands)
    {
        store.PushBatch(commands);
        Expect(store.Pump() == commands.Length && commands.All(command => command.Done.IsSet),
            "one scheduled batch must complete every submitted command");
    }

    private static bool ProbeRepairLock(InventoryStore store, ItemPage page)
    {
        if (!store.PageLocks.TryEnter(page.PageIndex)) return false;
        store.PageLocks.Exit(page.PageIndex);
        return true;
    }

    private static void Batch_SharedPageUseExcludesRepair()
    {
        using var store = CreateBatchTestStore();
        var first = store.AllocatePage();
        var second = store.AllocatePage();
        var firstSlot = BatchSlot(first);

        Expect(store.PageLocks.TryEnterUse(first.PageIndex), "external shared use must enter");
        try
        {
            var query = CommandFactory.QuerySlot(new[] { firstSlot });
            PumpBatch(store, query);
            Expect(query.Result.IsOk && !ProbeRepairLock(store, first),
                "a batch can share an in-use page and must leave the external use intact");
        }
        finally { store.PageLocks.ExitUse(first.PageIndex); }

        Expect(store.PageLocks.TryEnter(first.PageIndex), "repair must acquire a page after shared use ends");
        try
        {
            var query = CommandFactory.QuerySlot(new[] { firstSlot });
            PumpBatch(store, query);
            Expect(query.Result.Status == CommandStatus.PageBusy, "exclusive repair must block a batch's shared use");
        }
        finally { store.PageLocks.Exit(first.PageIndex); }

        var repairEnteredBetweenCommands = true;
        var repairEnteredAfterBatch = false;
        var firstQuery = DeferredBatchQuery(firstSlot);
        var probe = CommandFactory.Escape(new[] { BatchSlot(second) }, _ =>
        {
            repairEnteredBetweenCommands = ProbeRepairLock(store, first);
            return CommandResult.Ok();
        }, timeoutMs: BatchDeferred);
        var boundary = CommandFactory.QueryType(11, callback: _ => repairEnteredAfterBatch = ProbeRepairLock(store, first));
        PumpBatch(store, firstQuery, probe, boundary);
        Expect(firstQuery.Result.IsOk && probe.Result.IsOk && !repairEnteredBetweenCommands,
            "a page used by the preceding command must remain protected from repair until the batch ends");
        Expect(repairEnteredAfterBatch, "batch use must be released before result callbacks");
    }

    private static void Batch_StrongSamePageCommandsRunInFifoOrder()
    {
        using var store = CreateBatchTestStore();
        var page = store.AllocatePage();
        var from = BatchSlot(page);
        var to = BatchSlot(page, 1);
        var put = CommandFactory.Put(new[] { Stack(11, 7) }, new[] { from }, timeoutMs: BatchDeferred);
        var before = DeferredBatchQuery(from, to);
        var move = CommandFactory.Move(from, to, timeoutMs: BatchDeferred);
        var after = DeferredBatchQuery(from, to);
        var boundary = CommandFactory.QueryType(11);

        PumpBatch(store, put, before, move, after, boundary);
        Expect(put.Result.IsOk && move.Result.IsOk, "both same-page mutations must succeed");
        Expect(before.Result.IsOk && before.Result.TryGetResultPayload<SlotInfo[]>(out var first) &&
            first[0].Stack == 7 && first[1].IsAir, "the first query must see the Put before the Move");
        Expect(after.Result.IsOk && after.Result.TryGetResultPayload<SlotInfo[]>(out var second) &&
            second[0].IsAir && second[1].Stack == 7, "the second query must see the Move's destination");
    }

    private static void Batch_WeakTakeUsesHeldPageBeforeExpanding()
    {
        using var store = CreateBatchTestStore();
        var unheld = store.AllocatePage();
        var held = store.AllocatePage();
        var unheldStack = unheld.Slots[0] = Stack(11, 8);
        var heldStack = held.Slots[0] = Stack(11, 2);
        store.SyncSummaries(new HashSet<int> { unheld.PageIndex, held.PageIndex });

        var query = DeferredBatchQuery(BatchSlot(held));
        var take = CommandFactory.Take(11, 2);
        PumpBatch(store, query, take);

        Expect(query.Result.IsOk && take.Result.IsOk && take.Result.TryGetResultPayload<List<Item>>(out var items) &&
            items.Count == 1 && ReferenceEquals(items[0], heldStack), "weak Take must use the batch-held page first");
        Expect(held.Slots[0].IsAir && ReferenceEquals(unheld.Slots[0], unheldStack) && unheldStack.stack == 8,
            "a satisfied Take must leave the higher-capacity unheld page untouched");
    }

    private static void Batch_WeakPutUsesHeldPageBeforeExpanding()
    {
        using var store = CreateBatchTestStore();
        var unheld = store.AllocatePage();
        var held = store.AllocatePage();
        held.Slots[0] = Stack(12, 999);
        store.SyncSummaries(new HashSet<int> { held.PageIndex });

        var query = DeferredBatchQuery(BatchSlot(held));
        var put = CommandFactory.Put(new[] { Stack(11, 3) });
        PumpBatch(store, query, put);

        Expect(query.Result.IsOk && put.Result.IsOk && held.Slots[1].type == 11 && held.Slots[1].stack == 3,
            "weak Put must use an empty slot on the batch-held page first");
        Expect(unheld.Summary.OccupiedStacks == 0 && store.PageCache.AllocatedPages.Count == 2,
            "a satisfied Put must leave the emptier unheld page unused without allocating a page");
    }

    private static void Batch_WeakTakePrefersUsableCapacityAndStableTies()
    {
        using var store = CreateBatchTestStore(enableAggregate: false);
        var lessUsable = store.AllocatePage();
        // Insert the higher-index tie before the lower-index one to exercise explicit tie-breaking.
        var secondBest = store.PageCache.Directory.GetOrCreate(3);
        secondBest.EnsureSlots();
        var firstBest = store.AllocatePage();
        lessUsable.Slots[0] = Stack(11, 999);
        lessUsable.Slots[0].favorited = true;
        lessUsable.Slots[1] = Stack(11, 1);
        var firstStack = firstBest.Slots[0] = Stack(11, 3);
        var secondStack = secondBest.Slots[0] = Stack(11, 3);
        store.SyncSummaries(new HashSet<int> { secondBest.PageIndex, firstBest.PageIndex, lessUsable.PageIndex });

        var take = CommandFactory.Take(11, 3, avoidFavorited: true);
        PumpBatch(store, take);

        Expect(take.Result.IsOk && take.Result.TryGetResultPayload<List<Item>>(out var items) &&
            items.Count == 1 && ReferenceEquals(items[0], firstStack),
            "weak Take must choose the most usable non-favorited quantity and break ties by page index");
        Expect(lessUsable.Slots[0].stack == 999 && lessUsable.Slots[1].stack == 1 &&
            ReferenceEquals(secondBest.Slots[0], secondStack), "unselected Take pages must retain their stacks");
    }

    private static void Batch_WeakPutPrefersFreeSlotsAndStableTies()
    {
        using var store = CreateBatchTestStore();
        var fewerSlots = store.AllocatePage();
        var secondBest = store.PageCache.Directory.GetOrCreate(3);
        secondBest.EnsureSlots();
        var firstBest = store.AllocatePage();
        fewerSlots.Slots[0] = Stack(12, 999);
        store.SyncSummaries(new HashSet<int> { fewerSlots.PageIndex });

        var put = CommandFactory.Put(new[] { Stack(11, 3) });
        PumpBatch(store, put);

        Expect(put.Result.IsOk && firstBest.Slots[0].type == 11 && firstBest.Slots[0].stack == 3,
            "weak Put must choose the page with most free slots and break ties by page index");
        Expect(fewerSlots.Summary.OccupiedStacks == 1 && secondBest.Summary.OccupiedStacks == 0 &&
            store.PageCache.AllocatedPages.Count == 3, "unselected Put pages must remain untouched");
    }

    private static void Batch_WeakTakeSkipsRepairPageAndCompletes()
    {
        using var store = CreateBatchTestStore();
        var repairing = store.AllocatePage();
        var available = store.AllocatePage();
        repairing.Slots[0] = Stack(11, 20);
        available.Slots[0] = Stack(11, 3);
        store.SyncSummaries(new HashSet<int> { repairing.PageIndex, available.PageIndex });

        Expect(store.PageLocks.TryEnter(repairing.PageIndex), "repair must acquire the preferred page");
        try
        {
            var take = CommandFactory.Take(11, 3);
            PumpBatch(store, take);
            Expect(take.Result.IsOk && take.Result.TryGetResultPayload<List<Item>>(out var items) &&
                items.Count == 1 && items[0].stack == 3 && available.Slots[0].IsAir,
                "a fully satisfied weak Take must succeed after skipping the repairing page");
            Expect(repairing.Slots[0].stack == 20 && !repairing.LoadFailed,
                "a busy candidate must be preserved and must not be marked failed");
        }
        finally { store.PageLocks.Exit(repairing.PageIndex); }
    }

    private static void Batch_FailedPutLeavesSlotAvailableForNextCommand()
    {
        using var store = CreateBatchTestStore();
        var page = store.AllocatePage();
        var slot = BatchSlot(page);
        var rejectedItems = new[] { Stack(11, 4), Stack(12, 5) };
        var rejected = CommandFactory.Put(rejectedItems, new[] { slot }, timeoutMs: BatchDeferred);
        var accepted = CommandFactory.Put(new[] { Stack(13, 6) }, new[] { slot }, timeoutMs: BatchDeferred);
        var query = DeferredBatchQuery(slot);
        var take = CommandFactory.Take(13, 6);

        PumpBatch(store, rejected, accepted, query, take);

        Expect((rejected.Result.Status & CommandStatus.NoSpace) != 0 &&
            rejected.Result.TryGetResultPayload<List<Item>>(out var returned) && returned.SequenceEqual(rejectedItems),
            "failed Put must return both originals after rejecting the tentative placement");
        Expect(accepted.Result.IsOk && query.Result.IsOk && query.Result.TryGetResultPayload<SlotInfo[]>(out var slots) &&
            slots[0].Type == 13 && slots[0].Stack == 6, "the next strong Put must be able to reuse the exact slot");
        Expect(take.Result.IsOk && take.Result.TryGetResultPayload<List<Item>>(out var taken) &&
            taken.Count == 1 && taken[0].type == 13 && taken[0].stack == 6 && page.Slots[0].IsAir,
            "the same batch's weak Take must see the replacement and remove it");
        Expect(ProbeRepairLock(store, page), "a batch containing a failed command must release shared use");
    }
}
