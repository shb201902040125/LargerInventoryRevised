using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using LargerInventory.Backend;
using LargerInventory.Backend.Commands;
using LargerInventory.Backend.Integration;
using LargerInventory.Backend.Storage;

namespace LargerInventory.Tests;

internal static partial class Program
{
    private static int _passed;
    private static int _failed;

    private static int Main()
    {
        Run(nameof(PageDirectory_AllocatesAfterRestoredPages));
        Run(nameof(PageDirectory_ReusesGapsWithoutTouchingExistingPages));
        Run(nameof(PageStore_ListsBackupOnlyPages));
        Run(nameof(PageStore_PromotesValidBackup));
        Run(nameof(StoreMeta_KeepsFailedPageIndexAllocated));
        Run(nameof(FailedPage_IsRemovedFromTypeIndexAfterAccess));
        Run(nameof(Store_ReconcilesOnePagePerPumpBeforeAcceptingCommands));
        Run(nameof(Repair_OverwritesCorruptedPageWithBlank));
        Run(nameof(Repair_LockFailureIsReported));
        Run(nameof(PageLock_BusyCommandReturnsPageBusy));
        Run(nameof(CommandsPaused_RejectsNewCommands));
        Run(nameof(Config_TestCommandsDisabledByDefault));
        Run(nameof(Command_InputAndResultPayloadTypesAreDistinct));
        Run(nameof(Serializers_RoundTripFlagsAndSlotInfo));
        Run(nameof(Delivery_PutsOriginalItemIntoInventory));
        Run(nameof(Store_PumpExecutesOnCallingThread));
        Run(nameof(Store_PumpAfterCreationOnOtherThread));
        Run(nameof(Store_FlushWorksFromWorkerThread));
        Run(nameof(Store_ThrottlesWhenQueueFull));
        Run(nameof(Store_PushAfterDisposeFailsImmediately));
        Run(nameof(Store_DisposeDrainsQueuedCommands));
        Run(nameof(Store_RejectedPutReturnsAllItems));
        Run(nameof(Executor_MoveSameSlotIsInvalid));
        Run(nameof(Executor_NegativeSlotIsInvalid));
        Run(nameof(Executor_InvalidSlotQueryReturnsAirEntry));

        RunNetworkTests();
        RunStorageTests();
        RunCommandTests();
        RunQueueP0Tests();
        RunBatchPageTests();
        RunBatchConcurrencyTests();
        Console.WriteLine($"Passed: {_passed}, Failed: {_failed}");
        return _failed == 0 ? 0 : 1;
    }

    private static void Run(string name)
    {
        try
        {
            typeof(Program).GetMethod(name, System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(null, null);
            _passed++;
            Console.WriteLine($"[PASS] {name}");
        }
        catch (System.Reflection.TargetInvocationException e) when (e.InnerException is not null)
        {
            _failed++;
            Console.WriteLine($"[FAIL] {name}: {e.InnerException.Message}");
        }
    }

    private static void Expect(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void PageDirectory_AllocatesAfterRestoredPages()
    {
        var directory = new PageDirectory();
        directory.GetOrCreate(0);
        directory.GetOrCreate(1);
        directory.GetOrCreate(2);

        var allocated = directory.Allocate();
        Expect(allocated.PageIndex == 3, $"expected page 3, got {allocated.PageIndex}");
    }

    private static void PageDirectory_ReusesGapsWithoutTouchingExistingPages()
    {
        var directory = new PageDirectory();
        directory.GetOrCreate(0);
        directory.GetOrCreate(2);

        var first = directory.Allocate().PageIndex;
        var second = directory.Allocate().PageIndex;
        Expect(first == 1, $"expected gap page 1, got {first}");
        Expect(second == 3, $"expected page 3, got {second}");
    }

    private static void PageStore_PromotesValidBackup()
    {
        var root = Path.Combine(Path.GetTempPath(), "LargerInventory.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new FilePageFileStore(root);
            var blank = store.WriteBlankPage(0);
            Expect(blank.Success, "blank page write failed");

            var pages = Path.Combine(root, "pages");
            var primary = Path.Combine(pages, "0000000.lipg");
            var backup = primary + ".bak";
            File.Copy(primary, backup);
            File.WriteAllBytes(primary, new byte[] { 1, 2, 3 });

            var inspection = store.Inspect(0);
            Expect(inspection.State == PageFileState.RecoverableFromBackup,
                $"expected recoverable backup, got {inspection.State}");

            var promoted = store.PromoteBackup(0);
            Expect(promoted.Success, "backup promotion failed");
            Expect(store.Inspect(0).State == PageFileState.Valid, "promoted primary is not valid");
            Expect(File.Exists(backup), "promotion must keep the original backup");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void PageStore_ListsBackupOnlyPages()
    {
        var root = Path.Combine(Path.GetTempPath(), "LargerInventory.Tests", Guid.NewGuid().ToString("N"));
        var pages = Path.Combine(root, "pages");
        Directory.CreateDirectory(pages);
        try
        {
            File.WriteAllBytes(Path.Combine(pages, "0000007.lipg.bak"), new byte[] { 1 });
            var store = new FilePageFileStore(root);
            var listed = store.ListPages();
            Expect(listed.Contains(7), "page existing only as .bak was not enumerated");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void Serializers_RoundTripFlagsAndSlotInfo()
    {
        var command = CommandFactory.Take(12, 30, avoidFavorited: false);
        var commandTag = CommandSerializer.Save(command);
        var loaded = CommandSerializer.Load(commandTag);

        Expect(loaded.Kind == CommandKind.Take, $"kind {loaded.Kind}");
        var take = loaded.InputPayload as TakePayload;
        Expect(take is not null, "serialized take input type changed");
        Expect(take.Type == 12 && take.Count == 30, "take payload values changed");
        Expect(!take.AvoidFavorited, "take flag was lost");
        Expect(!loaded.Access.IsStrong && !loaded.Access.Weak.AvoidFavorited, "access flags were lost");

        var slots = new[]
        {
            new SlotInfo { SlotId = 5, Type = 12, Stack = 7, Favorited = true, IsAir = false },
            null
        };
        var resultTag = CommandResultSerializer.Save(CommandResult.Ok(slots));
        Expect(CommandResultSerializer.TryLoad(resultTag, out var result), "slot result did not deserialize");
        Expect(result.TryGetResultPayload<SlotInfo[]>(out var roundTripped), "slot result payload type changed");
        Expect(roundTripped.Length == 2, $"expected 2 slots, got {roundTripped.Length}");
        Expect(roundTripped[0] is not null && roundTripped[0].Favorited && !roundTripped[0].IsAir,
            "slot flags were lost");
        Expect(roundTripped[1] is not null && roundTripped[1].IsAir,
            "null slot entries must round-trip as air");

        var repair = new RepairReport
        {
            TotalPages = 3,
            RepairedPages = 2,
            RemainingFailedPages = 0,
            RepairedPageIndices = new[] { 0, 2 },
            LockFailedPageIndices = new[] { 5 }
        };
        var repairTag = CommandResultSerializer.Save(CommandResult.Ok(repair));
        Expect(CommandResultSerializer.TryLoad(repairTag, out var repairResult), "repair report did not deserialize");
        Expect(repairResult.TryGetResultPayload<RepairReport>(out var repairRoundTrip),
            "repair report payload type changed");
        Expect(repairRoundTrip.TotalPages == 3 && repairRoundTrip.RepairedPages == 2 &&
               repairRoundTrip.RepairedPageIndices.Length == 2,
            "repair report values changed");
        Expect(repairRoundTrip.LockFailedPageIndices.Length == 1 && repairRoundTrip.LockFailedPageIndices[0] == 5,
            "repair lock-failed page indices were lost");
    }

    private static void Delivery_PutsOriginalItemIntoInventory()
    {
        var player = new Terraria.Player { active = true };
        var start = Terraria.Main.InventoryItemSlotsStart;
        Expect(player.inventory is not null && player.inventory.Length > start, "player inventory is unavailable");

        for (var i = 0; i < player.inventory.Length; i++)
        {
            player.inventory[i] = new Terraria.Item();
        }

        var item = new Terraria.Item { type = 12, stack = 30, maxStack = 999 };
        var overflow = new List<Terraria.Item>();
        var delivered = PlayerItemDelivery.Give(player, new[] { item }, overflow);

        Expect(delivered == 30, $"expected 30 delivered, got {delivered}");
        Expect(overflow.Count == 0, "nothing should overflow with an empty inventory");
        Expect(ReferenceEquals(player.inventory[start], item), "delivery must place the original Item instance");
        Expect(player.inventory[start].stack == 30, "delivered stack must stay 30");
    }

    private static void StoreMeta_KeepsFailedPageIndexAllocated()
    {
        var root = Path.Combine(Path.GetTempPath(), "LargerInventory.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var failedPage = new ItemPage(0);
            failedPage.Summary.OccupiedBits[0] = 1UL;
            failedPage.Summary.SlotTypes[0] = 12;
            failedPage.Summary.Types[12] = new TypeStat { Count = 30 };
            failedPage.Summary.OccupiedStacks = 1;
            failedPage.LoadFailed = true;

            StoreMeta.Save(root, new[] { failedPage });

            var status = StoreMeta.Load(root, out var summaries, out var error);
            Expect(status == StoreMetaStatus.Valid, $"meta load failed: {status} {error}");
            Expect(summaries.TryGetValue(0, out var summary), "failed page entry was not persisted");
            Expect(summary.Unavailable, "failed page must stay marked unavailable");

            var store = new InventoryStore(new StoreConfig(), root, StoreScope.Player);
            store.Start();
            var allocated = store.AllocatePage();

            Expect(allocated.PageIndex == 1,
                $"allocator must not reuse a failed page index; got {allocated.PageIndex}");
            store.Dispose();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void FailedPage_IsRemovedFromTypeIndexAfterAccess()
    {
        var root = Path.Combine(Path.GetTempPath(), "LargerInventory.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var page = new ItemPage(0);
            page.Summary.OccupiedBits[0] = 1UL;
            page.Summary.SlotTypes[0] = 12;
            page.Summary.Types[12] = new TypeStat { Count = 30 };
            page.Summary.OccupiedStacks = 1;
            StoreMeta.Save(root, new[] { page });

            var store = new InventoryStore(new StoreConfig(), root, StoreScope.Player);
            store.Start();
            store.Pump();
            Expect(!store.IsPreparing, "store should be ready before querying its metadata summary");

            CommandResult? before = null;
            store.Push(CommandFactory.QueryType(12, 0, r => before = r));
            store.Pump();
            Expect(before is not null && before.Value.ResultPayload is QueryTypeResult q1 && q1.Count == 30,
                "meta summary should be indexed before the page is accessed");

            CommandResult? slot = null;
            store.Push(CommandFactory.QuerySlot(new[] { 0 }, r => slot = r));
            store.Pump();
            Expect(slot is not null && (slot.Value.Status & CommandStatus.PageLoadFailed) != 0,
                "missing page file must make the slot query fail");

            CommandResult? after = null;
            store.Push(CommandFactory.QueryType(12, 0, r => after = r));
            store.Pump();
            Expect(after is not null && after.Value.ResultPayload is QueryTypeResult q2 && q2.Count == 0,
                "unavailable page must be removed from the type index");

            store.Dispose();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void Store_ReconcilesOnePagePerPumpBeforeAcceptingCommands()
    {
        var root = Path.Combine(Path.GetTempPath(), "LargerInventory.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            _ = new FilePageFileStore(root);
            File.WriteAllBytes(Path.Combine(root, FilePageFileStore.FileName(0)),
                FilePageFileStore.Snapshot(0, null, new PageSummary()));
            File.WriteAllBytes(Path.Combine(root, FilePageFileStore.FileName(2)), new byte[] { 1, 2, 3 });
            File.WriteAllBytes(Path.Combine(root, FilePageFileStore.FileName(4)),
                FilePageFileStore.Snapshot(4, null, new PageSummary()));

            using var store = new InventoryStore(new StoreConfig { MaxReconcilePagesPerPump = 1 }, root);
            Expect(store.IsPreparing && store.PageCache.AllocatedPages.Count == 0,
                "construction must defer recovery of pages absent from metadata");

            store.Pump();
            Expect(store.IsPreparing && store.PageCache.AllocatedPages.Count == 1,
                "the first pump should reconcile one page");
            var item = new Terraria.Item { type = 12, stack = 1 };
            CommandResult? rejected = null;
            store.Push(CommandFactory.Put(new[] { item }, callback: result => rejected = result));
            Expect(rejected is { Status: CommandStatus.Preparing } preparing &&
                   preparing.TryGetResultPayload<List<Terraria.Item>>(out var returned) &&
                   returned.Count == 1 && ReferenceEquals(returned[0], item),
                "a put during preparation must return its original item immediately");
            Expect(store.FlushForSave(out var error), "clean flush during preparation failed: " + error);
            Expect(StoreMeta.Load(root, out _, out _) == StoreMetaStatus.Missing,
                "a save during preparation must not publish partial metadata");

            store.Pump();
            Expect(store.IsPreparing && store.PageCache.AllocatedPages.Count == 2 &&
                   store.PageCache.Directory.TryGet(2, out var failed) && failed.LoadFailed,
                "a corrupt page must consume a frame's budget and keep its index reserved");

            store.Pump();
            Expect(!store.IsPreparing && store.PageCache.AllocatedPages.Count == 3,
                "the store should become ready after the final page");
            CommandResult? query = null;
            store.Push(CommandFactory.QueryType(12, callback: result => query = result));
            store.Pump();
            Expect(query is { IsOk: true }, "the store should accept commands after reconciliation");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void Repair_OverwritesCorruptedPageWithBlank()
    {
        var root = Path.Combine(Path.GetTempPath(), "LargerInventory.Tests", Guid.NewGuid().ToString("N"));
        var pages = Path.Combine(root, "pages");
        Directory.CreateDirectory(pages);
        try
        {
            var page = new ItemPage(0);
            page.Summary.OccupiedBits[0] = 1UL;
            page.Summary.SlotTypes[0] = 12;
            page.Summary.Types[12] = new TypeStat { Count = 30 };
            page.Summary.OccupiedStacks = 1;
            StoreMeta.Save(root, new[] { page });

            // 主文件缺失、备份损坏：模拟“损坏且无可用备份”。
            File.WriteAllBytes(Path.Combine(pages, "0000000.lipg.bak"), new byte[] { 1, 2, 3 });

            var store = new InventoryStore(new StoreConfig(), root, StoreScope.Player);
            store.Start();

            RepairReport report = null;
            var started = store.StartPageRepair(_ => { }, r => report = r, out var error);
            Expect(started, $"repair failed to start: {error}");
            for (var i = 0; i < 1000 && report is null; i++)
            {
                store.Pump();
                Thread.Sleep(5);
            }

            Expect(report is not null, "repair did not complete");
            Expect(report.RepairedPages == 1, $"expected 1 repaired page, got {report.RepairedPages}");
            Expect(report.RepairedPageIndices.Length == 1 && report.RepairedPageIndices[0] == 0,
                "repaired page index mismatch");
            Expect(report.RemainingFailedPages == 0, "no failed page should remain");

            Expect(File.Exists(Path.Combine(pages, "0000000.lipg")), "blank page file was not written");
            Expect(!File.Exists(Path.Combine(pages, "0000000.lipg.bak")), "corrupted backup was not removed");

            CommandResult? query = null;
            store.Push(CommandFactory.QueryType(12, 0, r => query = r));
            store.Pump();
            Expect(query is not null && query.Value.TryGetResultPayload<QueryTypeResult>(out var result) && result.Count == 0,
                "repaired page must not retain old item counts");

            store.Dispose();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void Repair_LockFailureIsReported()
    {
        var root = Path.Combine(Path.GetTempPath(), "LargerInventory.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new InventoryStore(new StoreConfig(), root, StoreScope.Player);
            store.Start();
            var page = store.AllocatePage();

            using var locked = new ManualResetEventSlim(false);
            using var release = new ManualResetEventSlim(false);
            var thread = new Thread(() =>
            {
                Expect(store.PageLocks.TryEnter(page.PageIndex), "failed to take repair test lock");
                locked.Set();
                release.Wait(5000);
                store.PageLocks.Exit(page.PageIndex);
            });
            thread.Start();
            Expect(locked.Wait(5000), "repair lock thread did not start");

            RepairReport report = null;
            Expect(store.StartPageRepair(_ => { }, r => report = r, out var error), $"repair start failed: {error}");
            for (var i = 0; i < 1000 && report is null; i++)
            {
                store.Pump();
                Thread.Sleep(5);
            }

            Expect(report is not null, "repair did not complete");
            Expect(report.LockFailedPages == 1, $"expected 1 lock-failed page, got {report.LockFailedPages}");
            Expect(report.LockFailedPageIndices.Length == 1 && report.LockFailedPageIndices[0] == page.PageIndex,
                "lock-failed page index mismatch");

            release.Set();
            thread.Join(5000);
            store.Dispose();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void PageLock_BusyCommandReturnsPageBusy()
    {
        var store = new InventoryStore(new StoreConfig());
        store.Start();
        var page = store.AllocatePage();
        using var locked = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        var thread = new Thread(() =>
        {
            Expect(store.PageLocks.TryEnter(page.PageIndex), "failed to take test page lock");
            locked.Set();
            release.Wait(5000);
            store.PageLocks.Exit(page.PageIndex);
        });
        thread.Start();
        Expect(locked.Wait(5000), "page-lock thread did not start");

        try
        {
            CommandResult? result = null;
            store.Push(CommandFactory.QuerySlot(new[] { page.PageIndex << 10 }, r => result = r));
            store.Pump();

            Expect(result is not null, "query did not complete");
            Expect((result.Value.Status & CommandStatus.PageBusy) != 0,
                $"expected PageBusy, got {result.Value.Status}");
        }
        finally
        {
            release.Set();
            thread.Join(5000);
            store.Dispose();
        }
    }

    private static void CommandsPaused_RejectsNewCommands()
    {
        InventorySystem.ResumeAllCommands();
        InventorySystem.PauseAllCommands();
        try
        {
            var store = new InventoryStore(new StoreConfig());
            store.Start();

            CommandResult? rejected = null;
            store.Push(CommandFactory.QueryType(1, 0, r => rejected = r));
            Expect(rejected is not null, "paused store must reject immediately");
            Expect(rejected.Value.Status == CommandStatus.ManualInterrupt,
                $"expected ManualInterrupt, got {rejected.Value.Status}");
            store.Dispose();
        }
        finally
        {
            InventorySystem.ResumeAllCommands();
        }
    }

    private static void Config_TestCommandsDisabledByDefault()
    {
        var config = new LargerInventoryConfig();
        Expect(!config.EnableTestCommands, "test commands must be disabled by default");
    }

    private static void Command_InputAndResultPayloadTypesAreDistinct()
    {
        var command = CommandFactory.Take(12, 30);
        Expect(command.InputPayload is TakePayload, "Take input should be TakePayload");
        Expect(command.InputPayload is not List<Terraria.Item>, "Take input must not be confused with its item result");

        var items = new List<Terraria.Item>();
        var result = CommandResult.Ok(items);
        Expect(result.TryGetResultPayload<List<Terraria.Item>>(out var output), "Take output should be retrievable as an item list");
        Expect(ReferenceEquals(items, output), "result payload should preserve the returned item list");
    }

    private static void Store_PumpExecutesOnCallingThread()
    {
        var store = new InventoryStore(new StoreConfig());
        store.Start();

        var callbackThread = -1;
        CommandResult? received = null;
        store.Push(CommandFactory.QueryType(1, 0, result =>
        {
            callbackThread = Thread.CurrentThread.ManagedThreadId;
            received = result;
        }));

        Expect(received is null, "callback must not run before Pump");
        store.Pump();
        Expect(received is not null, "callback must run during Pump");
        Expect(callbackThread == Thread.CurrentThread.ManagedThreadId, "callback ran on a different thread");
        Expect(received.Value.IsOk, $"query status {received.Value.Status}");
        store.Dispose();
    }

    /// <summary>
    /// tModLoader 1.4.4 会在异步世界加载线程创建 Store。之后主线程的 Pump 必须照常工作。
    /// </summary>
    private static void Store_PumpAfterCreationOnOtherThread()
    {
        InventoryStore store = null;
        var thread = new Thread(() =>
        {
            store = new InventoryStore(new StoreConfig());
            store.Start();
        });
        thread.Start();
        thread.Join();

        Expect(store is not null, "store was not created");

        CommandResult? result = null;
        store.Push(CommandFactory.QueryType(1, 0, r => result = r));
        store.Pump();

        Expect(result is not null, "pump after cross-thread creation produced no result");
        Expect(result.Value.IsOk, $"status {result.Value.Status}");
        store.Dispose();
    }

    /// <summary>
    /// tModLoader 1.4.4 在后台线程执行 SaveData，FlushForSave 必须允许在非创建线程调用。
    /// </summary>
    private static void Store_FlushWorksFromWorkerThread()
    {
        var store = new InventoryStore(new StoreConfig());
        store.Start();

        var flushed = false;
        var thread = new Thread(() => { flushed = store.FlushForSave(out _); });
        thread.Start();
        thread.Join();

        Expect(flushed, "FlushForSave from a worker thread must succeed");
        store.Dispose();
    }

    private static void Store_ThrottlesWhenQueueFull()
    {
        var config = new StoreConfig { MaxQueuedCommands = 1 };
        var store = new InventoryStore(config);
        store.Start();

        store.Push(CommandFactory.QueryType(1));
        CommandResult? rejected = null;
        store.Push(CommandFactory.QueryType(2, 0, result => rejected = result));

        Expect(rejected is not null, "overflow command must fail immediately");
        Expect(rejected.Value.Status == CommandStatus.Throttled, $"status {rejected.Value.Status}");
        store.Dispose();
    }

    private static void Store_PushAfterDisposeFailsImmediately()
    {
        var store = new InventoryStore(new StoreConfig());
        store.Start();
        store.Dispose();

        CommandResult? rejected = null;
        store.Push(CommandFactory.QueryType(1, 0, result => rejected = result));
        Expect(rejected is not null, "push after dispose must complete immediately");
        Expect(rejected.Value.Status == CommandStatus.InternalError, $"status {rejected.Value.Status}");
    }

    private static void Store_DisposeDrainsQueuedCommands()
    {
        var store = new InventoryStore(new StoreConfig());
        store.Start();
        store.Pump();

        CommandResult? drained = null;
        store.Push(CommandFactory.QueryType(1, 0, result => drained = result));
        store.Dispose();

        Expect(drained is not null, "queued command must complete before Dispose returns");
        Expect(drained.Value.IsOk, $"drained status {drained.Value.Status}");
    }

    private static void Store_RejectedPutReturnsAllItems()
    {
        var store = new InventoryStore(new StoreConfig());
        store.Start();
        store.Dispose();

        CommandResult? rejected = null;
        store.Push(CommandFactory.Put(new List<Terraria.Item>(), true, 0, result => rejected = result));
        Expect(rejected is not null, "rejected put must complete immediately");
        Expect(rejected.Value.ResultPayload is List<Terraria.Item>, "rejected put must report its items as remaining");
    }

    private static void Executor_MoveSameSlotIsInvalid()
    {
        var store = new InventoryStore(new StoreConfig());
        store.Start();

        CommandResult? result = null;
        store.Push(CommandFactory.Move(5, 5, 0, r => result = r));
        store.Pump();

        Expect(result is not null, "move must complete");
        Expect(result.Value.Status == CommandStatus.InvalidArg, $"status {result.Value.Status}");
        store.Dispose();
    }

    private static void Executor_NegativeSlotIsInvalid()
    {
        var store = new InventoryStore(new StoreConfig());
        store.Start();

        CommandResult? result = null;
        store.Push(CommandFactory.QuerySlot(new[] { -1 }, r => result = r));
        store.Pump();

        Expect(result is not null, "query must complete");
        Expect((result.Value.Status & CommandStatus.InvalidArg) != 0, $"status {result.Value.Status}");
        store.Dispose();
    }

    private static void Executor_InvalidSlotQueryReturnsAirEntry()
    {
        var store = new InventoryStore(new StoreConfig());
        store.Start();

        CommandResult? result = null;
        store.Push(CommandFactory.QuerySlot(new[] { -1, int.MaxValue }, r => result = r));
        store.Pump();

        Expect(result is not null, "query must complete");
        Expect((result.Value.Status & CommandStatus.InvalidArg) != 0, $"status {result.Value.Status}");
        Expect(result.Value.TryGetResultPayload<SlotInfo[]>(out var slots), "query must return SlotInfo[]");
        Expect(slots.Length == 2, $"expected 2 entries, got {slots.Length}");
        foreach (var slot in slots)
        {
            Expect(slot is not null, "SlotInfo entries must never be null");
            Expect(slot.IsAir, "invalid slots must be reported as air");
        }

        Expect(store.PageCache.AllocatedPages.Count == 0,
            "invalid slot queries must not create pages");
        store.Dispose();
    }
}
