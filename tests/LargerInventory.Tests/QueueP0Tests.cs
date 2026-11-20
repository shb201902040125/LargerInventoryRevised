using System.Collections.Generic;
using System.Linq;
using System.Threading;
using LargerInventory.Backend;
using LargerInventory.Backend.Commands;

namespace LargerInventory.Tests;

internal static partial class Program
{
    private static void RunQueueP0Tests()
    {
        Run(nameof(Queue_ExplicitBoundariesTakePrecedenceAcrossCaps));
        Run(nameof(Queue_EarliestDeadlineIsFifoAndCapped));
        Run(nameof(Queue_TakeAllResetsSchedulingAndBatchCapacityIsAtomic));
        Run(nameof(Queue_PageBudgetYieldsAfterBatchAndDisposalDrains));
        Run(nameof(Queue_TimeBudgetIncludesCallbacks));
    }

    private static void Queue_ExplicitBoundariesTakePrecedenceAcrossCaps()
    {
        var queue = new CommandQueue();
        var earliest = CommandFactory.QueryType(1, timeoutMs: 1);
        var later = CommandFactory.QueryType(1, timeoutMs: int.MaxValue);
        var secondBoundary = CommandFactory.Boundary();
        var firstBoundary = CommandFactory.QueryType(1); // Any zero-timeout command is a boundary.
        queue.PushBatch(new[] { earliest, later, firstBoundary, secondBoundary });

        Expect(queue.TryTakeBatch(earliest.Deadline, 2, out var batch) && batch.SequenceEqual(new[] { earliest, later }),
            "first boundary must override already-due deadlines even when the cap truncates the prefix");
        Expect(queue.TryTakeBatch(long.MinValue, 20, out batch) && batch.SequenceEqual(new[] { firstBoundary }),
            "the truncated boundary must still be selected first");
        Expect(queue.TryTakeBatch(long.MinValue, 20, out batch) && batch.SequenceEqual(new[] { secondBoundary }),
            "only the first pending boundary may be selected");
    }

    private static void Queue_EarliestDeadlineIsFifoAndCapped()
    {
        var queue = new CommandQueue();
        var head = CommandFactory.QueryType(1, timeoutMs: int.MaxValue);
        var earlier = CommandFactory.QueryType(1, timeoutMs: 1);
        var tail = CommandFactory.QueryType(1, timeoutMs: int.MaxValue);
        queue.PushBatch(new[] { head, earlier, tail });

        Expect(!queue.TryTakeBatch(earlier.Deadline - 1, 10, out _) && queue.Count == 3,
            "nothing should leave the queue before the earliest deadline");
        Expect(queue.TryTakeBatch(earlier.Deadline, 0, out var batch) && batch.SequenceEqual(new[] { head }),
            "the minimum cap of one must take the FIFO head");
        Expect(queue.TryTakeBatch(earlier.Deadline, 10, out batch) && batch.SequenceEqual(new[] { earlier }),
            "the remaining due prefix ends at the earliest deadline");
        Expect(!queue.TryTakeBatch(earlier.Deadline, 10, out _) && queue.Count == 1,
            "the later deadline must remain pending");

        // A repeated object gives two independently enqueued positions with exactly equal deadlines.
        // This is a queue-level test. InventoryStore deduplicates submitted commands before enqueueing.
        queue.PushBatch(new[] { earlier, earlier });
        Expect(queue.TryTakeBatch(earlier.Deadline, 10, out batch) && batch.SequenceEqual(new[] { tail, earlier }),
            "equal deadlines must use the first queued position");
        Expect(queue.TryTakeBatch(earlier.Deadline, 10, out batch) && batch.SequenceEqual(new[] { earlier }),
            "removing the first equal deadline must leave the second tracked");
    }

    private static void Queue_TakeAllResetsSchedulingAndBatchCapacityIsAtomic()
    {
        var queue = new CommandQueue();
        var timed = CommandFactory.QueryType(1, timeoutMs: 1);
        var boundary = CommandFactory.Boundary();
        Expect(queue.TryPush(timed, 2), "first enqueue must fit");
        Expect(!queue.TryPushBatch(new[] { boundary, timed }, 2) && queue.Count == 1,
            "a batch over capacity must be rejected in full");
        Expect(!queue.TryTakeBatch(timed.Deadline - 1, 2, out _), "rejected boundaries must not affect scheduling");
        Expect(queue.TryPushBatch(new[] { boundary }, 2), "batch must fit exactly at capacity");
        Expect(queue.TakeAll().SequenceEqual(new[] { timed, boundary }) && queue.Count == 0,
            "TakeAll must return pending commands in FIFO order");
        Expect(queue.TryPush(timed, 2) && !queue.TryTakeBatch(timed.Deadline - 1, 2, out _),
            "TakeAll must clear out prior boundaries");
        Expect(queue.TryTakeBatch(timed.Deadline, 2, out var batch) && batch.SequenceEqual(new[] { timed }) &&
            !queue.TryTakeBatch(long.MaxValue, 2, out _), "queue must be reusable without stale positions");
    }

    private static void Queue_PageBudgetYieldsAfterBatchAndDisposalDrains()
    {
        using var store = new InventoryStore(new StoreConfig
        {
            MaxCommandPageWorkPerPump = 1,
            MaxCommandPumpMs = 60_000
        });
        var page = store.AllocatePage();
        var callbacks = new List<int>();
        var deferred = CommandFactory.QueryType(1, callback: _ => callbacks.Add(3));
        var timed = CommandFactory.QueryType(1, timeoutMs: int.MaxValue, callback: _ => callbacks.Add(1));
        var query = CommandFactory.QuerySlot(new[] { page.PageIndex << 10 }, _ =>
        {
            callbacks.Add(2);
            store.Push(CommandFactory.QueryType(1, timeoutMs: int.MaxValue, callback: _ => callbacks.Add(4)));
        });
        store.PushBatch(new[] { timed, query, deferred });

        Expect(store.Pump() == 2 && query.Result.IsOk && callbacks.SequenceEqual(new[] { 1, 2 }) && store.Queue.Count == 2,
            "the first scheduled batch must complete in FIFO order before yielding to the page-work budget");
        Expect(store.Pump() == 1 && callbacks.SequenceEqual(new[] { 1, 2, 3 }),
            "a new Pump must reset the budget and run the remaining immediate command first");
        Expect(store.Pump() == 0 && store.Queue.Count == 1, "callback-enqueued future work must still respect its deadline");
        store.Dispose();
        Expect(callbacks.SequenceEqual(new[] { 1, 2, 3, 4 }), "disposal must drain future work and notify each callback once");
    }

    private static void Queue_TimeBudgetIncludesCallbacks()
    {
        using var store = new InventoryStore(new StoreConfig { MaxCommandPumpMs = 1 });
        var callbackCount = 0;
        var first = CommandFactory.QueryType(1, callback: _ =>
        {
            Thread.Sleep(25);
            callbackCount++;
        });
        var second = CommandFactory.QueryType(1, callback: _ => callbackCount++);
        store.PushBatch(new[] { first, second });

        Expect(store.Pump() == 1 && callbackCount == 1 && !second.Done.IsSet,
            "time spent in the callback must count toward the yielding checkpoint");
        Expect(store.Pump() == 1 && callbackCount == 2 && second.Done.IsSet,
            "the next Pump must complete queued work exactly once");
    }
}
