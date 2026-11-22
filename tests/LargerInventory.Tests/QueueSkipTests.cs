using System;
using System.Collections.Generic;
using System.Linq;
using LargerInventory.Backend;
using LargerInventory.Backend.Commands;

namespace LargerInventory.Tests;

internal static partial class Program
{
    private static void RunQueueSkipTests()
    {
        Run(nameof(Queue_SkipMarkerReadiesOnlyTheOrdinaryPrefix));
        Run(nameof(Queue_SkipSegmentWaitsForExplicitBoundaryAndClearsCoveredIndexes));
        Run(nameof(Queue_TakeAllClearsSkipScheduling));
        Run(nameof(Store_PumpCompletesWholeSkipSegmentAfterLateBoundary));
        Run(nameof(Store_DisposeRejectsUnterminatedSkipSegment));
        Run(nameof(Store_SkipIfReadsPriorResultAndContinues));
        Run(nameof(Store_FailedPredicateReturnsSkippedPutInput));
    }

    private static void Queue_SkipMarkerReadiesOnlyTheOrdinaryPrefix()
    {
        var queue = new CommandQueue();
        var timed = CommandFactory.QueryType(1, timeoutMs: int.MaxValue);
        var implicitBoundary = CommandFactory.QueryType(1);
        var next = CommandFactory.QueryType(1, timeoutMs: int.MaxValue);
        var last = CommandFactory.QueryType(1, timeoutMs: int.MaxValue);
        var skip = new Command(CommandKind.SkipIf, int.MaxValue, null);
        queue.PushBatch(new[] { timed, implicitBoundary, next, last, skip });

        Expect(queue.TryTakeBatch(long.MinValue, 20, out var batch) && batch.SequenceEqual(new[] { timed, implicitBoundary }),
            "the first ordinary boundary must still win before a SkipIf marker");
        Expect(queue.TryTakeBatch(long.MinValue, 1, out batch) && batch.SequenceEqual(new[] { next }),
            "a SkipIf marker must ready the preceding prefix under the ordinary cap");
        Expect(queue.TryTakeBatch(long.MinValue, 20, out batch) && batch.SequenceEqual(new[] { last }),
            "the rest of the ordinary prefix must leave without taking the marker");
        Expect(!queue.TryTakeBatch(long.MaxValue, 20, out _) && queue.Count == 1,
            "an expired marker at the head must still wait for a later explicit boundary");
    }

    private static void Queue_SkipSegmentWaitsForExplicitBoundaryAndClearsCoveredIndexes()
    {
        var queue = new CommandQueue();
        var earlierBoundary = CommandFactory.Boundary();
        var skip = new Command(CommandKind.SkipIf, 0, null);
        var immediate = CommandFactory.QueryType(1);
        var nested = new Command(CommandKind.SkipIf, 0, null);
        var anotherImmediate = CommandFactory.QueryType(1);
        var timed = CommandFactory.QueryType(1, timeoutMs: 1);
        queue.PushBatch(new[] { earlierBoundary, skip, immediate, nested, anotherImmediate, timed });

        Expect(queue.TryTakeBatch(long.MaxValue, 10, out var batch) && batch.SequenceEqual(new[] { earlierBoundary }),
            "a preceding explicit boundary must leave separately");
        Expect(!queue.TryTakeBatch(long.MaxValue, 100, out _) && queue.Count == 5,
            "an earlier explicit boundary, nested SkipIf, implicit boundaries and deadlines cannot terminate the segment");

        var terminator = new Command(CommandKind.NopBoundary, int.MaxValue, null);
        var trailing = CommandFactory.QueryType(1, timeoutMs: int.MaxValue);
        var nextBoundary = CommandFactory.Boundary();
        queue.Push(terminator);
        queue.PushBatch(new[] { trailing, nextBoundary });

        Expect(queue.TryTakeBatch(long.MinValue, 1, out batch) &&
            batch.SequenceEqual(new[] { skip, immediate, nested, anotherImmediate, timed, terminator }),
            "the first later NopBoundary must close the whole segment regardless of cap or timeout");
        Expect(queue.TryTakeBatch(long.MinValue, 1, out batch) && batch.SequenceEqual(new[] { trailing }),
            "the next ready boundary must stay indexed after the earlier segment leaves");
        Expect(queue.TryTakeBatch(long.MinValue, 1, out batch) && batch.SequenceEqual(new[] { nextBoundary }),
            "a later explicit boundary must remain available to ordinary batching");
        Expect(!queue.TryTakeBatch(long.MaxValue, 10, out _) && queue.Count == 0,
            "no covered ready boundary or deadline may survive the segment");
    }

    private static void Queue_TakeAllClearsSkipScheduling()
    {
        var queue = new CommandQueue();
        var skip = new Command(CommandKind.SkipIf, 0, null);
        var boundary = CommandFactory.Boundary();
        queue.PushBatch(new[] { skip, boundary });
        Expect(queue.TakeAll().SequenceEqual(new[] { skip, boundary }),
            "TakeAll must return even an intact segment in FIFO order");

        var newSkip = new Command(CommandKind.SkipIf, 0, null);
        var ordinary = CommandFactory.QueryType(1);
        queue.PushBatch(new[] { newSkip, ordinary });
        Expect(!queue.TryTakeBatch(long.MaxValue, 10, out _),
            "TakeAll must clear the old explicit boundary before positions are reused");
        Expect(queue.TakeAll().SequenceEqual(new[] { newSkip, ordinary }),
            "TakeAll must extract a segment that is still waiting for its terminator");
        var timed = CommandFactory.QueryType(1, timeoutMs: int.MaxValue);
        queue.Push(timed);
        Expect(!queue.TryTakeBatch(long.MinValue, 10, out _) &&
            queue.TryTakeBatch(timed.Deadline, 10, out var batch) && batch.SequenceEqual(new[] { timed }),
            "ordinary scheduling must resume after TakeAll clears the marker");
    }

    private static void Store_PumpCompletesWholeSkipSegmentAfterLateBoundary()
    {
        using var store = new InventoryStore(new StoreConfig { MaxBatchCommands = 1, MaxBatchesPerPump = 1 });
        var callbacks = new List<(int, CommandStatus)>();
        var evaluations = 0;
        var prefix = CommandFactory.QueryType(1, timeoutMs: int.MaxValue, callback: result => callbacks.Add((1, result.Status)));
        var skip = CommandFactory.SkipIf(() => { evaluations++; return true; }, result => callbacks.Add((2, result.Status)));
        var enclosed = CommandFactory.QueryType(1, callback: result => callbacks.Add((3, result.Status)));
        store.PushBatch(new[] { prefix, skip, enclosed });

        Expect(store.Pump() == 1 && prefix.Done.IsSet && callbacks.SequenceEqual(new[] { (1, CommandStatus.Ok) }),
            "the ready ordinary prefix must complete in its own batch");
        Expect(store.Pump() == 0 && evaluations == 0 && !skip.Done.IsSet && !enclosed.Done.IsSet,
            "the segment must wait without evaluating the predicate or completing enclosed commands");

        var boundary = CommandFactory.Boundary();
        var afterBoundary = CommandFactory.QueryType(1, callback: result => callbacks.Add((4, result.Status)));
        store.Push(boundary);
        store.Push(afterBoundary);
        Expect(store.Pump() == 3 && boundary.Done.IsSet && !afterBoundary.Done.IsSet && evaluations == 1 &&
            callbacks.SequenceEqual(new[] { (1, CommandStatus.Ok), (2, CommandStatus.Ok), (3, CommandStatus.Skipped) }),
            "one Pump must execute the complete segment over the cap and leave subsequent work pending");
        Expect(store.Pump() == 1 && afterBoundary.Result.IsOk && callbacks.Last() == (4, CommandStatus.Ok),
            "commands after the explicit boundary must execute normally");
    }

    private static void Store_SkipIfReadsPriorResultAndContinues()
    {
        using var store = new InventoryStore(new StoreConfig { MaxBatchCommands = 1, MaxBatchesPerPump = 1 });
        var page = store.AllocatePage();
        var slot = page.PageIndex << 10;
        var input = Stack(11, 4);
        var put = CommandFactory.Put(new[] { input }, new[] { slot }, timeoutMs: int.MaxValue);
        var skip = CommandFactory.SkipIf(() => !put.Done.IsSet || !put.Result.IsOk);
        var take = CommandFactory.Take(11, 4);
        store.PushBatch(new[] { put, skip, take, CommandFactory.Boundary() });

        Expect(store.Pump() == 1 && put.Result.IsOk && !skip.Done.IsSet,
            "ordinary prefix must publish its result before the predicate runs");
        Expect(store.Pump() == 3 && skip.Result.TryGetResultPayload<bool>(out var skipped) && !skipped &&
            take.Result.TryGetResultPayload<List<Terraria.Item>>(out var taken) && ReferenceEquals(taken.Single(), input),
            "a false predicate must continue, and whole-stack transfers must retain item identity");
    }

    private static void Store_FailedPredicateReturnsSkippedPutInput()
    {
        using var store = new InventoryStore();
        var input = Stack(11, 4);
        var skip = CommandFactory.SkipIf(() => throw new InvalidOperationException("test predicate"));
        var put = CommandFactory.Put(new[] { input });
        var boundary = CommandFactory.Boundary();
        store.PushBatch(new[] { skip, put, boundary });

        Expect(store.Pump() == 3 && skip.Result.Status == CommandStatus.InternalError && boundary.Result.IsOk &&
            put.Result.Status == CommandStatus.Skipped &&
            put.Result.TryGetResultPayload<List<Terraria.Item>>(out var returned) && ReferenceEquals(returned.Single(), input),
            "predicate failure must skip the Put, return its original item, and reach the terminator");
    }

    private static void Store_DisposeRejectsUnterminatedSkipSegment()
    {
        using var store = new InventoryStore(new StoreConfig());
        var statuses = new List<CommandStatus>();
        var skip = new Command(CommandKind.SkipIf, 0, null, callback: result => statuses.Add(result.Status));
        var ordinary = CommandFactory.QueryType(1, callback: result => statuses.Add(result.Status));
        store.PushBatch(new[] { skip, ordinary });
        Expect(store.Pump() == 0 && statuses.Count == 0 && store.Queue.Count == 2,
            "Pump must leave an unterminated SkipIf segment wholly queued");
        store.Dispose();
        Expect(skip.Done.IsSet && ordinary.Done.IsSet && store.Queue.Count == 0 &&
            statuses.SequenceEqual(new[] { CommandStatus.InternalError, CommandStatus.InternalError }),
            "disposal must report failure for each pending command in an unterminated segment");
    }
}
