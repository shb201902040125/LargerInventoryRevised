using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using LargerInventory.Backend;
using LargerInventory.Backend.Commands;
using Terraria;

namespace LargerInventory.Tests;

internal static partial class Program
{
    private static readonly TimeSpan ConcurrentBatchWait = TimeSpan.FromSeconds(5);

    private static void RunBatchConcurrencyTests()
    {
        Run(nameof(BatchConcurrent_SharedReadsOverlapAndUseDetachedSnapshots));
        Run(nameof(BatchConcurrent_DisjointWritesOverlapAndBufferCallbacksInFifoOrder));
        Run(nameof(BatchConcurrent_ConflictingCommandsObserveWritesInFifoOrder));
        Run(nameof(BatchConcurrent_WeakQuerySeesPriorWritesBeforeLaterWrites));
    }

    private static InventoryStore CreateConcurrentBatchStore() => new(new StoreConfig
    {
        MaxBatchesPerPump = 1,
        MaxConcurrentCommandsPerBatch = 4,
        EnableGlobalAggregate = true
    });

    private static void BatchConcurrent_SharedReadsOverlapAndUseDetachedSnapshots()
    {
        using var store = CreateConcurrentBatchStore();
        var page = store.AllocatePage();
        var live = page.Slots[0] = Stack(11, 5);
        var slots = new[] { BatchSlot(page) };
        using var firstChangedSnapshot = new ManualResetEventSlim();
        using var secondReadSnapshot = new ManualResetEventSlim();
        Item firstSnapshot = null;
        Item secondSnapshot = null;
        var secondCount = -1;
        var overlapped = false;

        var first = CommandFactory.EscapeConcurrent(slots, readOnly: true, items =>
        {
            firstSnapshot = items.Single();
            firstSnapshot.stack = 99;
            firstChangedSnapshot.Set();
            overlapped = secondReadSnapshot.Wait(ConcurrentBatchWait);
            return CommandResult.Ok();
        }, timeoutMs: BatchDeferred);
        var second = CommandFactory.EscapeConcurrent(slots, readOnly: true, items =>
        {
            if (firstChangedSnapshot.Wait(ConcurrentBatchWait))
            {
                secondSnapshot = items.Single();
                secondCount = secondSnapshot.stack;
            }
            secondReadSnapshot.Set();
            return CommandResult.Ok();
        }, timeoutMs: BatchDeferred);

        PumpBatch(store, first, second, CommandFactory.Boundary());

        Expect(first.Result.IsOk && second.Result.IsOk && overlapped,
            "two read-only escapes on the same slot must overlap");
        Expect(secondCount == 5 && !ReferenceEquals(firstSnapshot, secondSnapshot) &&
            !ReferenceEquals(firstSnapshot, live) && !ReferenceEquals(secondSnapshot, live) && page.Slots[0].stack == 5,
            "read-only escapes must have independent snapshots and must not persist mutations");
    }

    private static void BatchConcurrent_DisjointWritesOverlapAndBufferCallbacksInFifoOrder()
    {
        using var store = CreateConcurrentBatchStore();
        var page = store.AllocatePage();
        var firstLive = page.Slots[0] = Stack(11, 3);
        page.Slots[1] = Stack(12, 4);
        var firstSlot = BatchSlot(page);
        var secondSlot = BatchSlot(page, 1);
        using var firstEntered = new ManualResetEventSlim();
        using var secondWrote = new ManualResetEventSlim();
        var callbackOrder = new ConcurrentQueue<int>();
        var overlapped = false;
        var secondEnteredInTime = false;
        var barrierRan = 0;
        var firstSnapshotIsDetached = false;
        var firstCountDuringAction = -1;

        var first = CommandFactory.EscapeConcurrent(new[] { firstSlot }, readOnly: false, items =>
        {
            var firstSnapshot = items.Single();
            firstSnapshotIsDetached = !ReferenceEquals(firstSnapshot, firstLive);
            firstSnapshot.stack = 7;
            firstEntered.Set();
            overlapped = secondWrote.Wait(ConcurrentBatchWait);
            return CommandResult.Ok();
        }, timeoutMs: BatchDeferred, callback: result =>
            callbackOrder.Enqueue(result.IsOk && Volatile.Read(ref barrierRan) == 1 ? 1 : -1));
        var second = CommandFactory.EscapeConcurrent(new[] { secondSlot }, readOnly: false, items =>
        {
            secondEnteredInTime = firstEntered.Wait(ConcurrentBatchWait);
            firstCountDuringAction = firstLive.stack;
            items.Single().stack = 8;
            secondWrote.Set();
            return CommandResult.Ok();
        }, timeoutMs: BatchDeferred, callback: result =>
            callbackOrder.Enqueue(result.IsOk && Volatile.Read(ref barrierRan) == 1 ? 2 : -2));
        var barrier = CommandFactory.Escape(new[] { firstSlot }, _ =>
        {
            Volatile.Write(ref barrierRan, 1);
            return CommandResult.Ok();
        }, callback: result => callbackOrder.Enqueue(result.IsOk ? 3 : -3));

        PumpBatch(store, first, second, barrier);

        Expect(first.Result.IsOk && second.Result.IsOk && overlapped && secondEnteredInTime,
            "disjoint write escapes on the same page must overlap");
        Expect(firstSnapshotIsDetached && firstCountDuringAction == 3 &&
            page.Slots[0].stack == 7 && page.Slots[1].stack == 8,
            "disjoint writes must use detached snapshots and commit changes to both slots");
        Expect(callbackOrder.SequenceEqual(new[] { 1, 2, 3 }),
            "callbacks must wait until batch execution ends and run in submission order");
    }

    private static void BatchConcurrent_ConflictingCommandsObserveWritesInFifoOrder()
    {
        using var store = CreateConcurrentBatchStore();
        var page = store.AllocatePage();
        page.Slots[0] = Stack(11, 3);
        var slots = new[] { BatchSlot(page) };
        var actionOrder = new ConcurrentQueue<int>();

        Command Observe(int order, bool readOnly, int delta) => CommandFactory.EscapeConcurrent(slots, readOnly, items =>
        {
            var item = items.Single();
            var before = item.stack;
            if (!readOnly) item.stack += delta;
            actionOrder.Enqueue(order);
            return CommandResult.Ok(before);
        }, timeoutMs: BatchDeferred);

        var before = Observe(1, readOnly: true, delta: 0);
        var firstWrite = Observe(2, readOnly: false, delta: 4);
        var secondWrite = Observe(3, readOnly: false, delta: 2);
        var after = Observe(4, readOnly: true, delta: 0);
        PumpBatch(store, before, firstWrite, secondWrite, after, CommandFactory.Boundary());

        var commands = new[] { before, firstWrite, secondWrite, after };
        Expect(commands.All(command => command.Result.IsOk) && actionOrder.SequenceEqual(new[] { 1, 2, 3, 4 }),
            "overlapping read-write, write-write and write-read pairs must run in FIFO order");
        Expect(commands.Select(command => command.Result.ResultPayload).SequenceEqual(new object[] { 3, 3, 7, 9 }) &&
            page.Slots[0].stack == 9, "each conflicting command must observe preceding writes before executing");
    }

    private static void BatchConcurrent_WeakQuerySeesPriorWritesBeforeLaterWrites()
    {
        using var store = CreateConcurrentBatchStore();
        var page = store.AllocatePage();
        page.Slots[0] = Stack(11, 3);
        page.Slots[1] = Stack(11, 4);
        store.SyncSummaries(new HashSet<int> { page.PageIndex });

        Command Write(int slot, int count) => CommandFactory.EscapeConcurrent(new[] { slot }, readOnly: false, items =>
        {
            items.Single().stack = count;
            return CommandResult.Ok();
        }, timeoutMs: BatchDeferred);

        var first = Write(BatchSlot(page), 7);
        var barrier = CommandFactory.QueryType(11, timeoutMs: BatchDeferred);
        var second = Write(BatchSlot(page, 1), 8);
        var after = CommandFactory.QueryType(11);
        PumpBatch(store, first, barrier, second, after);

        Expect(first.Result.IsOk && second.Result.IsOk, "both writes around the weak query must succeed");
        Expect(barrier.Result.IsOk && barrier.Result.TryGetResultPayload<QueryTypeResult>(out var between) &&
            between.Count == 11, "a weak query in the middle of a batch must see only preceding writes");
        Expect(after.Result.IsOk && after.Result.TryGetResultPayload<QueryTypeResult>(out var final) &&
            final.Count == 15, "the final weak query must see both writes");
    }
}
