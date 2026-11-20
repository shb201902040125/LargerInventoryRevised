using System.Collections.Generic;
using System.Linq;
using System.Threading;
using LargerInventory.Backend;
using LargerInventory.Backend.Commands;
using LargerInventory.Backend.Storage;
using Terraria;

namespace LargerInventory.Tests;

internal static partial class Program
{
    private static void RunCommandTests()
    {
        Run(nameof(Command_StrongPutNeverSpills));
        Run(nameof(Command_PutRejectsEntireBatchWhenOnlyPartFits));
        Run(nameof(Command_PutReleasesTentativePagesOnRejection));
        Run(nameof(Command_DifferentTypeMovePreservesBothSlots));
        Run(nameof(Command_BusyPutReturnsOriginalInput));
        Run(nameof(Command_DuplicateSubmissionSettlesOnce));
        Run(nameof(Command_TypeIndexRecoversAfterSaturation));
    }

    private static Item Stack(int type, int count) => new() { type = type, stack = count, maxStack = 999 };

    private static void Command_StrongPutNeverSpills()
    {
        using var store = new InventoryStore();
        var page = store.AllocatePage();
        page.Slots[0] = Stack(11, 2);
        var input = Stack(12, 5);
        var command = CommandFactory.Put(new[] { input }, new[] { 0 }, spill: true);
        store.Push(command);
        store.Pump();

        Expect(command.Result.Status == (CommandStatus.NoSpace | CommandStatus.TargetOccupied | CommandStatus.TypeMismatch),
            $"strong put status {command.Result.Status}");
        Expect(command.Result.TryGetResultPayload<List<Item>>(out var remaining) && ReferenceEquals(remaining.Single(), input),
            "blocked input must be returned in full");
        Expect(page.Slots[0].stack == 2 && page.Slots[1].IsAir && store.PageCache.AllocatedPages.Count == 1,
            "strong put cannot modify unlisted slots or allocate pages");
    }

    private static void Command_PutRejectsEntireBatchWhenOnlyPartFits()
    {
        using var store = new InventoryStore();
        var page = store.AllocatePage();
        var first = Stack(11, 5);
        var second = Stack(12, 3);
        var command = CommandFactory.Put(new[] { first, second }, new[] { 0 }, spill: true);
        store.Push(command);
        store.Pump();

        Expect(command.Result.Status == (CommandStatus.NoSpace | CommandStatus.TargetOccupied | CommandStatus.TypeMismatch),
            $"put status {command.Result.Status}");
        Expect(command.Result.TryGetResultPayload<List<Item>>(out var returned) && returned.SequenceEqual(new[] { first, second }),
            "rejected Put must return both original stacks");
        Expect(page.Slots[0].IsAir && !page.Dirty && page.Summary.OccupiedStacks == 0,
            "capacity rejection must leave the slot and summary unchanged");
    }

    private static void Command_PutReleasesTentativePagesOnRejection()
    {
        using var store = new InventoryStore();
        var page = store.AllocatePage();
        for (var slot = 0; slot < ItemPage.SlotCount - 1; slot++)
            page.Slots[slot] = Stack(11, 999);
        page.Summary.RebuildFrom(page.Slots);

        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var worker = new Thread(() =>
        {
            if (!store.PageLocks.TryEnter(1)) return;
            try
            {
                entered.Set();
                release.Wait();
            }
            finally { store.PageLocks.Exit(1); }
        });
        worker.Start();
        try
        {
            Expect(entered.Wait(5000), "worker must lock the next page");
            var inputs = new[] { Stack(12, 999), Stack(13, 1) };
            var command = CommandFactory.Put(inputs, spill: true);
            store.Push(command);
            store.Pump();
            Expect((command.Result.Status & CommandStatus.PageBusy) != 0, $"put status {command.Result.Status}");
            Expect(command.Result.TryGetResultPayload<List<Item>>(out var returned) && returned.SequenceEqual(inputs),
                "rejected expansion must return the original stacks");
            Expect(page.Slots[^1].IsAir && store.PageCache.AllocatedPages.Count == 1,
                "rejected expansion must restore the last slot and discard the tentative page");
        }
        finally
        {
            release.Set();
            worker.Join();
        }
        Expect(store.AllocatePage().PageIndex == 1, "the rejected operation must leave the page index reusable");
    }

    private static void Command_DifferentTypeMovePreservesBothSlots()
    {
        using var store = new InventoryStore();
        var page = store.AllocatePage();
        var source = page.Slots[0] = Stack(11, 2);
        var destination = page.Slots[1] = Stack(12, 5);
        var command = CommandFactory.Move(0, 1);
        store.Push(command);
        store.Pump();

        Expect(command.Result.Status == (CommandStatus.TargetOccupied | CommandStatus.TypeMismatch),
            $"move status {command.Result.Status}");
        Expect(ReferenceEquals(page.Slots[0], source) && ReferenceEquals(page.Slots[1], destination),
            "both stacks must remain in place");
    }

    private static void Command_BusyPutReturnsOriginalInput()
    {
        using var store = new InventoryStore();
        var page = store.AllocatePage();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var worker = new Thread(() =>
        {
            if (!store.PageLocks.TryEnter(page.PageIndex)) return;
            try
            {
                entered.Set();
                release.Wait();
            }
            finally
            {
                store.PageLocks.Exit(page.PageIndex);
            }
        });
        worker.Start();
        try
        {
            Expect(entered.Wait(5000), "worker must acquire the page lock");
            var input = Stack(11, 3);
            var command = CommandFactory.Put(new[] { input }, new[] { 0 });
            store.Push(command);
            store.Pump();
            Expect(command.Result.Status == CommandStatus.PageBusy, $"busy put status {command.Result.Status}");
            Expect(command.Result.TryGetResultPayload<List<Item>>(out var remaining) && ReferenceEquals(remaining.Single(), input),
                "unexecuted put must return its original input");
            Expect(page.Slots[0].IsAir, "busy page must remain untouched");
        }
        finally
        {
            release.Set();
            worker.Join();
        }
    }

    private static void Command_DuplicateSubmissionSettlesOnce()
    {
        using var store = new InventoryStore();
        var page = store.AllocatePage();
        var calls = 0;
        var command = CommandFactory.Put(new[] { Stack(11, 3) }, new[] { 0 }, callback: _ => calls++);
        store.Push(command);
        store.PushBatch(new[] { command, command });
        store.Pump();
        store.Push(command);
        Expect(calls == 1 && page.Slots[0].stack == 3, "duplicate submissions must not transfer or settle again");
    }

    private static void Command_TypeIndexRecoversAfterSaturation()
    {
        var index = new TypeIndex();
        var empty = new Dictionary<int, TypeStat>();
        var first = new Dictionary<int, TypeStat> { [11] = new() { ExactCount = int.MaxValue } };
        var second = new Dictionary<int, TypeStat> { [11] = new() { ExactCount = 100 } };
        index.UpdatePage(0, empty, first);
        index.UpdatePage(1, empty, second);
        Expect(index.TryGet(11, out var count, out _, out _, out var saturated) && count == int.MaxValue && saturated,
            "count must be saturated on output");
        index.UpdatePage(0, first, empty);
        Expect(index.TryGet(11, out count, out _, out _, out saturated) && count == 100 && !saturated,
            "removing a page must recover the exact remaining count");
    }
}
