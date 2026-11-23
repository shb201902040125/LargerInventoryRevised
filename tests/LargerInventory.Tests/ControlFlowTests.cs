using System;
using System.Collections.Generic;
using System.Linq;
using LargerInventory.Backend;
using LargerInventory.Backend.Commands;
using Terraria;

namespace LargerInventory.Tests;

internal static partial class Program
{
    private static void RunControlFlowTests()
    {
        Run(nameof(ControlFlow_ForwardJumpReturnsSkippedPutInput));
        Run(nameof(ControlFlow_BackwardJumpReadsLatestTakeAndPublishesOnce));
        Run(nameof(ControlFlow_CrossingRangesIgnoreInternalBoundaries));
        Run(nameof(ControlFlow_MissingEndpointsRejectWholeSubmission));
        Run(nameof(ControlFlow_SharedLabelRejectsWholeSubmission));
        Run(nameof(ControlTransaction_AbortReturnsOriginalInput));
        Run(nameof(ControlFlow_CommandBudgetChecksMergedRange));
        Run(nameof(ControlFlow_StepBudgetEndsLoopAndPreservesVisitedResults));
        Run(nameof(ControlTransaction_RestoreSavepointAndCommit));
        Run(nameof(ControlTransaction_RollbackRestoresExistingStackAndOwnership));
        Run(nameof(ControlTransaction_RollbackRemovesNewPageAfterStepLimit));
    }

    private static void ControlFlow_SharedLabelRejectsWholeSubmission()
    {
        using var store = new InventoryStore();
        var target = new BranchLabel();
        var commands = new[]
        {
            CommandFactory.Br(target, _ => false),
            CommandFactory.Br(target, _ => false),
            CommandFactory.Label(target)
        };
        store.PushBatch(commands);
        Expect(store.Pump() == 0 && commands.All(command => command.Result.Status == CommandStatus.InvalidArg),
            "a label must pair with exactly one branch");
    }

    private static void ControlTransaction_AbortReturnsOriginalInput()
    {
        using var store = new InventoryStore();
        var input = Stack(11, 4);
        var put = CommandFactory.Put(new[] { input });
        var settled = false;
        CommandResult completion = default;
        store.PushControlTransaction(new[] { put }, _ =>
        {
            settled = true;
            return ControlTransactionDecision.Commit;
        }, result => completion = result);
        store.AbortCommands(CommandStatus.ManualInterrupt);
        Expect(!settled && completion.Status == CommandStatus.ManualInterrupt && put.Done.IsSet &&
            put.Result.TryGetResultPayload<List<Item>>(out var returned) && ReferenceEquals(returned.Single(), input),
            "aborting queued transactions must return original input without executing the settlement decision");
    }

    private static void ControlFlow_ForwardJumpReturnsSkippedPutInput()
    {
        using var store = new InventoryStore(new StoreConfig { MaxBatchCommands = 1, MaxBatchesPerPump = 1 });
        var page = store.AllocatePage();
        var input = Stack(11, 4);
        var target = new BranchLabel();
        var completions = new List<CommandStatus>();
        var jump = CommandFactory.Br(target, _ => true);
        var put = CommandFactory.Put(new[] { input }, new[] { page.PageIndex << 10 },
            callback: result => completions.Add(result.Status));
        var label = CommandFactory.Label(target);
        var suffix = CommandFactory.QueryType(11, callback: result => completions.Add(result.Status));
        store.PushBatch(new[] { jump, put, label, suffix });

        Expect(store.Pump() == 3 && jump.Result.TryGetResultPayload<bool>(out var taken) && taken &&
            label.Result.IsOk && !suffix.Done.IsSet, "the forward range must complete together beyond the ordinary batch cap");
        Expect(put.Result.Status == CommandStatus.Skipped &&
            put.Result.TryGetResultPayload<List<Item>>(out var returned) && ReferenceEquals(returned.Single(), input) &&
            page.Slots[0].IsAir, "the skipped Put must leave the slot empty and return the original input");
        Expect(store.Pump() == 1 && suffix.Result.IsOk &&
            completions.SequenceEqual(new[] { CommandStatus.Skipped, CommandStatus.Ok }),
            "the ordinary suffix must complete in the next batch");
    }

    private static void ControlFlow_BackwardJumpReadsLatestTakeAndPublishesOnce()
    {
        using var store = new InventoryStore(new StoreConfig { MaxBatchCommands = 1 });
        var slot = store.AllocatePage().PageIndex << 10;
        var seed = CommandFactory.Put(new[] { Stack(11, 5) }, new[] { slot });
        store.Push(seed);
        Expect(store.Pump() == 1 && seed.Result.IsOk, "loop setup must store the initial stack");

        var start = new BranchLabel();
        var observations = new List<long>();
        var callbacks = new List<string>();
        var callbacksDuringExecution = 0;
        var take = CommandFactory.Take(11, 2, callback: _ => callbacks.Add("take"));
        var jump = CommandFactory.Br(start, context =>
        {
            callbacksDuringExecution += callbacks.Count;
            if (!context.TryGetResult(take, out var result) || !result.TryGetResultPayload<TakePreview>(out var preview))
                return false;
            observations.Add(preview.Count);
            return preview.Count == 2;
        }, _ => callbacks.Add("branch"));
        store.PushBatch(new[] { CommandFactory.Label(start), take, jump });

        Expect(store.Pump() == 3 && observations.SequenceEqual(new long[] { 2, 2, 1 }),
            "the backward branch must observe the latest Take count and fall through after the final partial Take");
        Expect(callbacksDuringExecution == 0 && callbacks.SequenceEqual(new[] { "take", "branch" }) &&
            take.Result.TryGetResultPayload<List<Item>>(out var delivered) && delivered.Sum(item => item.stack) == 5,
            "the loop must publish each callback once in FIFO order and deliver all executed Take output");
    }

    private static void ControlFlow_CrossingRangesIgnoreInternalBoundaries()
    {
        using var store = new InventoryStore(new StoreConfig { MaxBatchCommands = 1, MaxBatchesPerPump = 1 });
        var first = new BranchLabel();
        var second = new BranchLabel();
        var prefix = CommandFactory.QueryType(11, timeoutMs: int.MaxValue);
        var firstJump = CommandFactory.Br(first, _ => false);
        var boundary = CommandFactory.Boundary();
        var secondJump = CommandFactory.Br(second, _ => false);
        var firstLabel = CommandFactory.Label(first);
        var interior = CommandFactory.QueryType(11);
        var secondLabel = CommandFactory.Label(second);
        var suffix = CommandFactory.QueryType(11);
        store.PushBatch(new[] { prefix, firstJump, boundary, secondJump, firstLabel, interior, secondLabel, suffix });

        Expect(store.Pump() == 1 && prefix.Done.IsSet && !firstJump.Done.IsSet,
            "the range must ready the ordinary prefix as a separate batch");
        Expect(store.Pump() == 6 && boundary.Result.IsOk && interior.Result.IsOk && secondLabel.Done.IsSet &&
            !suffix.Done.IsSet, "crossing intervals must merge and execute together despite the internal boundaries");
        Expect(store.Pump() == 1 && suffix.Result.IsOk, "the ordinary suffix must remain runnable");
    }

    private static void ControlFlow_MissingEndpointsRejectWholeSubmission()
    {
        using var store = new InventoryStore();
        var target = new BranchLabel();
        var evaluated = false;
        var input = Stack(11, 4);
        var put = CommandFactory.Put(new[] { input });
        var jump = CommandFactory.Br(target, _ => evaluated = true);
        var label = CommandFactory.Label(target);
        store.PushBatch(new[] { jump, put });
        store.Push(label);

        Expect(store.Pump() == 0 && !evaluated && new[] { jump, put, label }.All(command =>
            command.Done.IsSet && command.Result.Status == CommandStatus.InvalidArg),
            "endpoints in separate submissions must reject both submissions without evaluating the predicate");
        Expect(put.Result.TryGetResultPayload<List<Item>>(out var returned) && ReferenceEquals(returned.Single(), input),
            "a rejected submission must return the original Put input");
    }

    private static void ControlFlow_CommandBudgetChecksMergedRange()
    {
        using var store = new InventoryStore(new StoreConfig { MaxControlFlowCommands = 4 });
        var first = new BranchLabel();
        var second = new BranchLabel();
        var submission = new[] { CommandFactory.QueryType(11), CommandFactory.Br(first, _ => false),
            CommandFactory.Br(second, _ => false), CommandFactory.Label(first), CommandFactory.Boundary(),
            CommandFactory.Label(second) };
        store.PushBatch(submission);

        Expect(store.Pump() == 0 && submission.All(command => command.Done.IsSet && command.Result.Status == CommandStatus.InvalidArg),
            "individually legal ranges that merge above the command budget must reject the entire submission");
    }

    private static void ControlFlow_StepBudgetEndsLoopAndPreservesVisitedResults()
    {
        const int stepLimit = 6;
        using var store = new InventoryStore(new StoreConfig { MaxControlFlowSteps = stepLimit, MaxBatchesPerPump = 1 });
        var start = new BranchLabel();
        var end = new BranchLabel();
        var evaluations = 0;
        var input = Stack(11, 4);
        var unvisited = CommandFactory.Put(new[] { input });
        var loop = CommandFactory.Br(start, _ => { evaluations++; return true; });
        var flow = new[] { CommandFactory.Label(start), CommandFactory.Br(end, _ => false), loop, unvisited,
            CommandFactory.Label(end) };
        var suffix = CommandFactory.QueryType(11);
        store.PushBatch(flow.Append(suffix).ToArray());

        Expect(store.Pump() == flow.Length && evaluations == 2 && flow.All(command => command.Done.IsSet) && loop.Result.IsOk,
            "the budget must count every instruction visit, including labels, and preserve executed results");
        Expect(unvisited.Result.Status == CommandStatus.ControlFlowLimit &&
            unvisited.Result.TryGetResultPayload<List<Item>>(out var returned) && ReferenceEquals(returned.Single(), input) &&
            flow[^1].Result.Status == CommandStatus.ControlFlowLimit,
            "step exhaustion must settle unvisited instructions and return unconsumed input");
        Expect(store.Pump() == 1 && suffix.Result.IsOk, "step exhaustion must leave subsequent work runnable");
    }

    private static void ControlTransaction_RestoreSavepointAndCommit()
    {
        using var store = new InventoryStore(new StoreConfig { MaxBatchCommands = 1, MaxBatchesPerPump = 1 });
        var page = store.AllocatePage();
        var input = Stack(11, 7);
        var callbacks = new List<string>();
        var put = CommandFactory.Put(new[] { input }, new[] { page.PageIndex << 10 }, callback: _ => callbacks.Add("put"));
        var keptTake = CommandFactory.Take(11, 2, callback: _ => callbacks.Add("kept take"));
        ControlSavepoint savepoint = null;
        var save = CommandFactory.TransactionAction(context => savepoint = context.CreateSavepoint());
        var discardedTake = CommandFactory.Take(11, 3, callback: _ => callbacks.Add("discarded take"));
        var sawDiscardedTake = false;
        var restoredResults = false;
        var slot = page.PageIndex << 10;
        var restore = CommandFactory.TransactionAction(context =>
        {
            sawDiscardedTake = context.TryGetResult(discardedTake, out var result) &&
                result.TryGetResultPayload<TakePreview>(out var preview) && preview.Count == 3 &&
                preview.Selections.SequenceEqual(new[] { new TakeSelection(slot, 3) });
            context.Restore(savepoint);
            restoredResults = !context.TryGetResult(discardedTake, out _) &&
                context.TryGetResult(keptTake, out var kept) && kept.TryGetResultPayload<TakePreview>(out var keptPreview) &&
                keptPreview.Count == 2;
        });
        var restoredSlot = CommandFactory.QuerySlot(new[] { slot });
        var restoredType = CommandFactory.QueryType(11);
        var body = new[] { put, keptTake, save, discardedTake, restore, restoredSlot, restoredType };
        var settledBeforePublication = false;
        CommandResult? completion = null;
        store.PushControlTransaction(body, context =>
        {
            settledBeforePublication = context.ExecutionStatus == CommandStatus.Ok && callbacks.Count == 0 &&
                body.All(command => !command.Done.IsSet) && context.TryGetResult(put, out var result) && result.IsOk;
            return ControlTransactionDecision.Commit;
        }, result =>
        {
            completion = result;
            callbacks.Add("transaction");
        });
        var suffix = CommandFactory.QueryType(11);
        store.Push(suffix);

        Expect(store.Pump() > 0 && completion is { IsOk: true } && sawDiscardedTake && restoredResults && settledBeforePublication,
            "the unsplit transaction must make speculative results available and restore the checkpoint before publishing");
        Expect(restoredSlot.Result.TryGetResultPayload<SlotInfo[]>(out var restoredSlots) &&
            restoredSlots[0].Stack == 7 && restoredSlots[0].AvailableStack == 5 &&
            restoredType.Result.TryGetResultPayload<QueryTypeResult>(out var restoredCount) && restoredCount.Count == 5,
            "restoring the savepoint must release the discarded quantity while keeping the earlier reservation");
        Expect(!suffix.Done.IsSet && put.Result.IsOk && keptTake.Result.IsOk &&
            keptTake.Result.TryGetResultPayload<List<Item>>(out var keptItems) && keptItems.Sum(item => item.stack) == 2 &&
            discardedTake.Result.Status == CommandStatus.Skipped &&
            discardedTake.Result.TryGetResultPayload<List<Item>>(out var discardedItems) && discardedItems.Count == 0 &&
            callbacks.SequenceEqual(new[] { "put", "kept take", "discarded take", "transaction" }),
            "commit must publish the surviving output and complete each command before the transaction callback");
        Expect(store.Pump() == 1 && suffix.Result.TryGetResultPayload<QueryTypeResult>(out var final) && final.Count == 5 &&
            page.Slots[0].stack == 5, "the restored stack and its summary must include only writes through the savepoint");
    }

    private static void ControlTransaction_RollbackRestoresExistingStackAndOwnership()
    {
        using var store = new InventoryStore();
        var page = store.AllocatePage();
        var slots = new[] { page.PageIndex << 10 };
        var original = Stack(11, 6);
        var seed = CommandFactory.Put(new[] { original }, slots);
        store.Push(seed);
        Expect(store.Pump() == 1 && seed.Result.IsOk, "rollback setup must store the initial stack");

        var input = Stack(12, 4);
        var take = CommandFactory.Take(11, 6);
        var put = CommandFactory.Put(new[] { input }, new[] { slots[0] + 1 });
        var speculativeQuery = CommandFactory.QueryType(12);
        var observedTentativeState = false;
        CommandResult? completion = null;
        store.PushControlTransaction(new[] { take, put, speculativeQuery }, context =>
        {
            observedTentativeState = context.TryGetResult(speculativeQuery, out var result) &&
                result.TryGetResultPayload<QueryTypeResult>(out var count) && count.Count == 4 &&
                context.TryGetResult(put, out var putResult) &&
                putResult.TryGetResultPayload<PutPreview>(out var preview) && preview == new PutPreview(4, 0);
            return ControlTransactionDecision.Rollback;
        }, result => completion = result);

        Expect(store.Pump() > 0 && completion is { Status: CommandStatus.RolledBack } && observedTentativeState &&
            new[] { take, put, speculativeQuery }.All(command => command.Done.IsSet && command.Result.Status == CommandStatus.RolledBack),
            "rollback must settle the envelope and every body command, including the query that saw a tentative mutation");
        Expect(take.Result.TryGetResultPayload<List<Item>>(out var delivered) && delivered.Count == 0 &&
            put.Result.TryGetResultPayload<List<Item>>(out var returned) && ReferenceEquals(returned.Single(), input) &&
            input.stack == 4 && ReferenceEquals(page.Slots[0], original) && original.stack == 6 && page.Slots[1].IsAir,
            "rollback must restore the exact original stack, suppress tentative Take output and return the original Put input");
        var originalCount = CommandFactory.QueryType(11);
        var rolledBackCount = CommandFactory.QueryType(12);
        store.PushBatch(new[] { originalCount, rolledBackCount });
        Expect(store.Pump() == 2 && originalCount.Result.TryGetResultPayload<QueryTypeResult>(out var restored) && restored.Count == 6 &&
            rolledBackCount.Result.TryGetResultPayload<QueryTypeResult>(out var removed) && removed.Count == 0,
            "rollback must correct both published type summaries");
    }

    private static void ControlTransaction_RollbackRemovesNewPageAfterStepLimit()
    {
        using var store = new InventoryStore(new StoreConfig { MaxControlFlowSteps = 4 });
        var input = Stack(11, 4);
        var put = CommandFactory.Put(new[] { input });
        var speculativeQuery = CommandFactory.QueryType(11);
        var target = new BranchLabel();
        var evaluations = 0;
        var loop = CommandFactory.Br(target, _ => { evaluations++; return true; });
        var body = new[] { put, speculativeQuery, CommandFactory.Label(target), loop };
        var sawTentativePage = false;
        var executionStatus = CommandStatus.Ok;
        CommandResult? completion = null;
        store.PushControlTransaction(body, context =>
        {
            executionStatus = context.ExecutionStatus;
            sawTentativePage = context.TryGetResult(speculativeQuery, out var result) &&
                result.TryGetResultPayload<QueryTypeResult>(out var count) && count.Count == 4;
            return ControlTransactionDecision.Rollback;
        }, result => completion = result);

        Expect(store.Pump() > 0 && executionStatus == CommandStatus.ControlFlowLimit && evaluations == 1 && sawTentativePage &&
            completion?.Status == (CommandStatus.ControlFlowLimit | CommandStatus.RolledBack) &&
            body.All(command => command.Done.IsSet && command.Result.Status == CommandStatus.RolledBack),
            "the settle callback must see the step limit and tentative results, and rollback must settle all commands");
        Expect(put.Result.TryGetResultPayload<List<Item>>(out var returned) && ReferenceEquals(returned.Single(), input) &&
            store.PageCache.AllocatedPages.Count == 0, "rollback must return the input and release the provisionally allocated page");
        var query = CommandFactory.QueryType(11);
        store.Push(query);
        Expect(store.Pump() == 1 && query.Result.TryGetResultPayload<QueryTypeResult>(out var finalCount) && finalCount.Count == 0 &&
            store.AllocatePage().PageIndex == 0, "rollback must clear the type index and make the page index reusable");
    }
}
