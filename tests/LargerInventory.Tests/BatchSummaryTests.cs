using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using LargerInventory.Backend;
using LargerInventory.Backend.Commands;
using LargerInventory.Backend.Storage;
using Terraria;

namespace LargerInventory.Tests;

internal static partial class Program
{
    private static void RunBatchSummaryTests()
    {
        Run(nameof(BatchSummary_CoalescesWritesAcrossStrongCommands));
        Run(nameof(BatchSummary_PublishesAtWeakBarriersAndPersistsFinalState));
        Run(nameof(PageEviction_StopsAtCapForFreshPages));
        Run(nameof(StrongSlotValidation_RejectsDuplicateSmallAndLargeSets));
    }

    private static void BatchSummary_CoalescesWritesAcrossStrongCommands()
    {
        foreach (var width in new[] { 1, 4 })
        {
            using var store = new InventoryStore(new StoreConfig
            {
                MaxBatchesPerPump = 1,
                MaxConcurrentCommandsPerBatch = width
            });
            var page = store.AllocatePage();
            var untouched = store.AllocatePage();
            var initialVersion = page.Summary.Version;
            var untouchedVersion = untouched.Summary.Version;
            var commands = new List<Command>();
            for (var i = 0; i < 6; i++)
            {
                commands.Add(CommandFactory.Put(new[] { Stack(11, i + 1) },
                    new[] { BatchSlot(page, i) }, timeoutMs: BatchDeferred));
            }

            var destination = BatchSlot(page, 6);
            commands.Add(CommandFactory.Move(BatchSlot(page), destination, timeoutMs: BatchDeferred));
            var read = DeferredBatchQuery(BatchSlot(page), destination);
            commands.Add(read);
            commands.Add(CommandFactory.Boundary());

            PumpBatch(store, commands.ToArray());

            Expect(commands.All(command => command.Result.IsOk), $"width {width}: every batch command must succeed");
            Expect(read.Result.TryGetResultPayload<SlotInfo[]>(out var slots) && slots[0].IsAir && slots[1].Stack == 1,
                $"width {width}: the strong reader must see the preceding move");
            Expect(page.Summary.Version - initialVersion == 1,
                $"width {width}: seven writes must publish one summary, got delta {page.Summary.Version - initialVersion}");
            Expect(untouched.Summary.Version == untouchedVersion && page.Summary.OccupiedStacks == 6 &&
                page.Summary.SlotTypes[0] == 0 && page.Summary.SlotTypes[6] == 11 &&
                page.Summary.Types.TryGetValue(11, out var total) && total.Count == 21,
                $"width {width}: the final summary must match the slots and leave the untouched page alone");
        }
    }

    private static void BatchSummary_PublishesAtWeakBarriersAndPersistsFinalState()
    {
        var root = Path.Combine(Path.GetTempPath(), "LargerInventory.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            int pageIndex;
            int finalVersion;
            using (var store = new InventoryStore(new StoreConfig { MaxBatchesPerPump = 1 }, root))
            {
                store.Pump();
                Expect(!store.IsPreparing, "the persistent store must be ready");
                var page = store.AllocatePage();
                pageIndex = page.PageIndex;
                var initialVersion = page.Summary.Version;
                var first = BatchSlot(page);
                var second = BatchSlot(page, 1);
                var third = BatchSlot(page, 2);
                var putFirst = CommandFactory.Put(new[] { Stack(11, 3) }, new[] { first }, timeoutMs: BatchDeferred);
                var putSecond = CommandFactory.Put(new[] { Stack(11, 4) }, new[] { second }, timeoutMs: BatchDeferred);
                var beforeTake = CommandFactory.QueryType(11, timeoutMs: BatchDeferred);
                var move = CommandFactory.Move(first, third, timeoutMs: BatchDeferred);
                var take = CommandFactory.Take(11, 7, timeoutMs: BatchDeferred);
                var afterTake = DeferredBatchQuery(first, second, third);
                var finalQuery = CommandFactory.QueryType(11);

                PumpBatch(store, putFirst, putSecond, beforeTake, move, take, afterTake, finalQuery);

                Expect(putFirst.Result.IsOk && putSecond.Result.IsOk && move.Result.IsOk,
                    "all strong mutations must succeed");
                Expect(beforeTake.Result.IsOk && beforeTake.Result.TryGetResultPayload<QueryTypeResult>(out var before) &&
                    before.Count == 7, "the type barrier must see both preceding writes");
                Expect(take.Result.IsOk && take.Result.TryGetResultPayload<List<Item>>(out var items) &&
                    items.Count == 2 && items.Sum(item => item.stack) == 7, "the weak take must find the moved stack too");
                Expect(afterTake.Result.IsOk && afterTake.Result.TryGetResultPayload<SlotInfo[]>(out var slots) &&
                    slots[0].IsAir && slots[1].Stack == 4 && !slots[1].IsAir &&
                    slots[2].Stack == 3 && !slots[2].IsAir && slots.All(slot => slot.AvailableStack == 0),
                    "the same-batch slot query must show physical stacks with their quantities reserved");
                Expect(finalQuery.Result.IsOk && finalQuery.Result.TryGetResultPayload<QueryTypeResult>(out var final) &&
                    final.Count == 0, "the final type query must see the removal");
                finalVersion = page.Summary.Version;
                Expect(finalVersion - initialVersion == 3,
                    $"the puts, move and take must publish one summary each, got delta {finalVersion - initialVersion}");
                Expect(store.FlushForSave(out var error), "could not flush the coalesced summary: " + error);
            }

            // An empty final page can be reopened by the console harness without game ItemIO hooks.
            Expect(StoreMeta.Load(root, out var summaries, out var metaError) == StoreMetaStatus.Valid,
                "could not read the saved metadata: " + metaError);
            Expect(summaries.TryGetValue(pageIndex, out var saved) && saved.Version == finalVersion &&
                saved.OccupiedStacks == 0 && saved.Types.Count == 0,
                "persisted metadata must contain the final summary and version");
            var loaded = new FilePageFileStore(root).Load(pageIndex);
            Expect(loaded.Success && loaded.Items.All(item => item is null || item.IsAir),
                "the persisted page must agree with the final empty summary");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void PageEviction_StopsAtCapForFreshPages()
    {
        var root = Path.Combine(Path.GetTempPath(), "LargerInventory.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            const int cap = 3;
            var cache = new PageCache(new StoreConfig
            {
                EnablePageEviction = true,
                MaxLoadedPages = cap,
                PageIdleUnloadMs = long.MaxValue
            }, new FilePageFileStore(root), new PageLockTable());
            var pages = Enumerable.Range(0, 6).Select(_ => cache.AllocatePage()).ToArray();
            cache.Pin(pages[0].PageIndex);
            cache.MarkDirty(pages[1]);

            cache.TrimToLimit();
            Expect(pages.Count(page => page.Loaded) == cap && pages[0].Loaded && pages[1].Loaded && pages[^1].Loaded,
                "trimming over cap must preserve pinned and dirty pages and evict clean pages in directory order");
            cache.UnpinAll();
            Expect(pages.Count(page => page.Loaded) == cap && cache.AllocatedPages.Count == pages.Length,
                "a second trim at cap must keep all fresh pages and retain the directory entries");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void StrongSlotValidation_RejectsDuplicateSmallAndLargeSets()
    {
        using var store = new InventoryStore();
        var page = store.AllocatePage();
        foreach (int size in new[] { 2, 9 })
        {
            var slots = Enumerable.Range(0, size).Select(i => BatchSlot(page, i)).ToArray();
            var valid = CommandFactory.Escape(slots, static _ => CommandResult.Ok());
            PumpBatch(store, valid);
            Expect(valid.Result.IsOk, $"{size} distinct slots should be accepted");

            slots[^1] = slots[0];
            var invalid = CommandFactory.Escape(slots, static _ => CommandResult.Ok());
            PumpBatch(store, invalid);
            Expect(invalid.Result.Status == CommandStatus.InvalidArg, $"duplicates in a {size}-slot set should be rejected");
        }
    }

    private static int RunWavesBenchmark()
    {
        const int commandsPerBatch = 64;
        const int warmupBatches = 32;
        const int measuredBatches = 256;
        Console.WriteLine($"Warm strong commands, {measuredBatches} batches of {commandsPerBatch} commands per mode");
        Console.WriteLine("Timings cover batch submission and Pump. All extension delegates are no-ops.");
        foreach (var workload in new[] { "moves", "borrowed escape", "cloned escape" })
        foreach (var requestedWidth in new[] { 1, 4, 8 })
        {
            var config = new StoreConfig
            {
                MaxBatchesPerPump = 1,
                MaxBatchCommands = commandsPerBatch,
                MaxConcurrentCommandsPerBatch = requestedWidth
            };
            using var store = new InventoryStore(config);
            var page = store.AllocatePage();
            for (var i = 0; i < commandsPerBatch; i++)
                page.Slots[2 * i] = Stack(11, 1);
            store.SyncSummaries(new HashSet<int> { page.PageIndex });

            Command[] CreateBatch(int batchIndex)
            {
                var side = batchIndex % 2;
                var batch = new Command[commandsPerBatch];
                for (var i = 0; i < batch.Length; i++)
                {
                    var from = BatchSlot(page, 2 * i + side);
                    var to = BatchSlot(page, 2 * i + (1 - side));
                    var timeout = i == batch.Length - 1 ? 0 : BatchDeferred;
                    batch[i] = workload == "moves"
                        ? CommandFactory.Move(from, to, timeoutMs: timeout, callback: static _ => { })
                        : CommandFactory.EscapeConcurrent([from], readOnly: true, static _ => CommandResult.Ok(),
                            timeoutMs: timeout, callback: static _ => { }, cloneItems: workload == "cloned escape");
                }
                return batch;
            }

            for (var i = 0; i < warmupBatches; i++)
                PumpBatch(store, CreateBatch(i));

            var batches = Enumerable.Range(warmupBatches, measuredBatches).Select(CreateBatch).ToArray();
            var samples = new double[measuredBatches];
            var completed = 0;
            var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
            var started = Stopwatch.GetTimestamp();
            for (var i = 0; i < batches.Length; i++)
            {
                var batchStarted = Stopwatch.GetTimestamp();
                store.PushBatch(batches[i]);
                completed += store.Pump();
                samples[i] = Stopwatch.GetElapsedTime(batchStarted).TotalMilliseconds;
            }
            var elapsed = Stopwatch.GetElapsedTime(started);
            var allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
            Expect(completed == measuredBatches * commandsPerBatch &&
                batches.All(batch => batch.All(command => command.Done.IsSet && command.Result.IsOk)),
                $"width {requestedWidth}: every measured command must complete successfully");
            Array.Sort(samples);
            double Percentile(double fraction) => samples[(int)Math.Ceiling(samples.Length * fraction) - 1];
            var label = requestedWidth == 1 ? "serial" : $"wave {requestedWidth}";
            Console.WriteLine($"{workload}, {label}: elapsed {elapsed.TotalMilliseconds:F2} ms, " +
                $"P95 {Percentile(0.95):F3} ms, P99 {Percentile(0.99):F3} ms, {allocated / completed:N0} B/command");
        }
        return 0;
    }
}
