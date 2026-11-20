using System;
using System.IO;
using System.Linq;
using System.Threading;
using LargerInventory.Backend;
using LargerInventory.Backend.Commands;
using LargerInventory.Backend.Storage;
using Terraria.ModLoader.IO;

namespace LargerInventory.Tests;

internal static partial class Program
{
    private static void RunStorageTests()
    {
        Run(nameof(Storage_ReplaysInterruptedPublication));
        Run(nameof(Storage_RejectsObsoletePageAndPreservesItsCopies));
        Run(nameof(Storage_ArchivesBothCorruptCopiesAndPreservesFutureVersions));
        Run(nameof(Storage_WorkerFlushCommitsEveryDirtyPage));
        Run(nameof(Storage_EncoderFailureRollsBackPageAndLedger));
        Run(nameof(Storage_AsyncPreMarkerFailureRollsBackOnOwner));
        Run(nameof(Storage_AsyncRollbackPreservesEarlierDirtyPages));
        Run(nameof(Storage_AsyncDurableLedgerFinalizesOnOwner));
        Run(nameof(Storage_PeriodicSaveSettlesOnOwner));
        Run(nameof(Storage_PeriodicSaveRefreshesMetadataThenSkipsCleanStore));
        Run(nameof(Storage_AsyncPreparationPreservesUnsupportedPageCopies));
        Run(nameof(Storage_AsyncPreparationRefusesUnsupportedMetadata));
        Run(nameof(Storage_FailedDisposeRetainsDirtyPageForRetry));
        Run(nameof(Storage_RejectsRemovedImportJournalTarget));
    }

    private static void Storage_ReplaysInterruptedPublication()
    {
        WithStorageDirectory(root =>
        {
            var files = new FilePageFileStore(root);
            var page = FilePageFileStore.Snapshot(0, null, new PageSummary());
            var pagePath = Path.Combine(root, FilePageFileStore.FileName(0));
            var ledgerPath = Path.Combine(root, StoreRedoJournal.LedgerName);
            var ledger = new byte[] { 12, 34, 56 };
            var journal = new StoreRedoJournal(root);
            // A directory on a publication target reliably interrupts replay after the page is published.
            Directory.CreateDirectory(ledgerPath);
            var complete = journal.Commit(new[]
            {
                new StoreRedoJournal.Write(FilePageFileStore.FileName(0), page),
                new StoreRedoJournal.Write(StoreRedoJournal.LedgerName, ledger)
            });
            Expect(!complete && Directory.Exists(Path.Combine(root, ".commit-pending")), "replayable marker missing after interruption");
            Expect(File.ReadAllBytes(pagePath).SequenceEqual(page), "first target not published before interruption");
            Directory.Delete(ledgerPath);
            using var recovered = new InventoryStore(new StoreConfig(), root);
            Expect(recovered.LoadTransactionLedger().SequenceEqual(ledger), "replay did not restore the ledger");
            Expect(files.ReadCopy(0, false, false).Success, "replay did not retain the page");
            Expect(!Directory.Exists(Path.Combine(root, ".commit-pending")), "replay marker not retired");
        });
    }

    private static void Storage_RejectsObsoletePageAndPreservesItsCopies()
    {
        WithStorageDirectory(root =>
        {
            var files = new FilePageFileStore(root);
            var obsolete = LegacyBlank(0);
            var path = Path.Combine(root, FilePageFileStore.FileName(0));
            var backup = FilePageFileStore.Snapshot(0, null, new PageSummary());
            File.WriteAllBytes(path, obsolete);
            File.WriteAllBytes(path + ".bak", backup);

            Expect(files.Load(0).State == PageFileState.UnsupportedVersion, "v1 page was loaded or masked by its backup");
            Expect(!files.WriteBlankPage(0).Success && !files.PromoteBackup(0).Success, "unsupported page was replaced");
            Expect(File.ReadAllBytes(path).SequenceEqual(obsolete) && File.ReadAllBytes(path + ".bak").SequenceEqual(backup),
                "refused writes must preserve both copies");
        });
    }

    private static void Storage_ArchivesBothCorruptCopiesAndPreservesFutureVersions()
    {
        WithStorageDirectory(root =>
        {
            var files = new FilePageFileStore(root);
            var first = Path.Combine(root, FilePageFileStore.FileName(0));
            var second = Path.Combine(root, FilePageFileStore.FileName(1));
            var primary = new byte[] { 9, 4, 3 };
            var backup = new byte[] { 8, 5, 1 };
            var future = FilePageFileStore.Snapshot(1, null, new PageSummary());
            future[4] = 127;
            File.WriteAllBytes(first, primary);
            File.WriteAllBytes(first + ".bak", backup);
            File.WriteAllBytes(second, future);
            using var store = new InventoryStore(new StoreConfig(), root);
            RepairReport report = null;
            Expect(store.StartPageRepair(null, result => report = result, out var error), "repair failed to start: " + error);
            for (var i = 0; i < 10 && report is null; i++) store.Pump();
            Expect(report is not null && report.RepairedPages == 1 && report.UnsupportedVersionPages == 1,
                "repair did not distinguish corruption and unsupported format");
            Expect(report.ArchiveDirectory is not null &&
                File.ReadAllBytes(Path.Combine(report.ArchiveDirectory, Path.GetFileName(first))).SequenceEqual(primary) &&
                File.ReadAllBytes(Path.Combine(report.ArchiveDirectory, Path.GetFileName(first) + ".bak")).SequenceEqual(backup),
                "repair archive did not preserve both copies");
            Expect(files.ReadCopy(0, false, true).Success && !File.Exists(first + ".bak"), "corrupt page was not replaced by blank");
            Expect(File.ReadAllBytes(second).SequenceEqual(future), "future page was modified");
        });
    }

    private static void Storage_WorkerFlushCommitsEveryDirtyPage()
    {
        WithStorageDirectory(root =>
        {
            using var store = new InventoryStore(new StoreConfig(), root);
            store.Pump();
            var first = store.AllocatePage();
            var second = store.AllocatePage();
            store.MarkDirty(first);
            store.MarkDirty(second);
            store.Pump();
            var saved = false;
            string error = null;
            var worker = new Thread(() => saved = store.FlushForSave(out error));
            worker.Start();
            Expect(worker.Join(5000) && saved, "worker flush failed: " + error);
            Expect(!first.Dirty && !second.Dirty, "dirty pages were not acknowledged");
            var files = new FilePageFileStore(root);
            Expect(files.ReadCopy(first.PageIndex, false, false).Success && files.ReadCopy(second.PageIndex, false, false).Success,
                "worker flush did not publish all dirty pages");
        });
    }

    private static void Storage_EncoderFailureRollsBackPageAndLedger()
    {
        WithStorageDirectory(root =>
        {
            using var store = new InventoryStore(new StoreConfig(), root);
            store.Pump();
            var originalLedger = new byte[] { 17 };
            Expect(store.CommitTransactionLedger(originalLedger, out var error), "baseline ledger rejected: " + error);
            var command = CommandFactory.QueryType(12, 0, null);
            var committed = store.TryExecuteTransaction(command, _ =>
            {
                // Force a distinct staged page without requiring tModLoader to initialize item serializers.
                store.MarkDirty(store.AllocatePage());
                throw new InvalidOperationException("Injected ledger encoder failure");
            }, out error);
            Expect(!committed && error == "Injected ledger encoder failure", "pre-marker failure was not returned");
            Expect(store.PageCache.AllocatedPages.Count == 0, "uncommitted page was retained after rollback");
            Expect(store.LoadTransactionLedger().SequenceEqual(originalLedger), "last committed ledger was replaced");
            Expect(!File.Exists(Path.Combine(root, FilePageFileStore.FileName(0))), "rolled-back page was published");
        });
    }

    private static void Storage_AsyncPreMarkerFailureRollsBackOnOwner()
    {
        WithStorageDirectory(root =>
        {
            using var store = new InventoryStore(new StoreConfig(), root);
            store.Pump();
            var originalLedger = new byte[] { 17 };
            Expect(store.CommitTransactionLedger(originalLedger, out var error), "baseline ledger rejected: " + error);
            var owner = Environment.CurrentManagedThreadId;
            var callbackThread = 0;
            var callbacks = 0;
            var success = true;
            var marker = Path.Combine(root, ".commit-pending");
            var command = CommandFactory.QueryType(12, 0, null);
            var accepted = store.TryExecuteTransactionAsync(command, _ =>
            {
                store.MarkDirty(store.AllocatePage());
                // A file at the journal marker path makes the worker fail before committing.
                File.WriteAllText(marker, "blocked");
                return new byte[] { 99 };
            }, (result, _) =>
            {
                callbacks++;
                callbackThread = Environment.CurrentManagedThreadId;
                success = result;
            }, out error);
            Expect(accepted && callbacks == 0 && store.IsBusy, "transaction must remain pending until owner finalization: " + error);
            var read = CommandFactory.QueryType(12, 0, null);
            store.Push(read);
            Expect(read.Result.Status == CommandStatus.PageBusy, "a read entered the pending transaction");
            var workerSaved = true;
            var workerClosed = true;
            var worker = new Thread(() =>
            {
                workerSaved = store.FlushForSave(out _);
                workerClosed = store.TryDispose(out _);
            });
            worker.Start();
            Expect(worker.Join(5000) && !workerSaved && !workerClosed && callbacks == 0 && !store.IsDisposed,
                "worker save or close took ownership of the pending transaction");
            Expect(SpinWait.SpinUntil(() => { store.Pump(); return callbacks > 0; }, 5000), "owner did not finalize failure");
            File.Delete(marker);
            store.Pump();
            Expect(callbacks == 1 && !success && callbackThread == owner, "failed result must run once on the owner thread");
            Expect(store.PageCache.AllocatedPages.Count == 0 && store.LoadTransactionLedger().SequenceEqual(originalLedger),
                "failed async transaction did not roll back");
        });
    }

    private static void Storage_AsyncRollbackPreservesEarlierDirtyPages()
    {
        WithStorageDirectory(root =>
        {
            using var store = new InventoryStore(new StoreConfig(), root);
            store.Pump();
            var earlier = store.AllocatePage();
            store.MarkDirty(earlier);
            var callbacks = 0;
            var failure = string.Empty;
            var encoderRan = false;
            var command = CommandFactory.QueryType(12, 0, null);
            Expect(store.TryExecuteTransactionAsync(command, _ =>
            {
                encoderRan = true;
                store.MarkDirty(store.AllocatePage());
                throw new InvalidOperationException("failed after earlier save");
            }, (success, commitError) =>
            {
                callbacks++;
                failure = success ? null : commitError;
            }, out var error), "transaction was rejected: " + error);
            Expect(!encoderRan && callbacks == 0, "transaction started before saving earlier dirty pages");
            Expect(store.FlushForSave(out error), "owner flush failed: " + error);
            Expect(callbacks == 1 && failure == "failed after earlier save", "transaction failure was not reported");
            var files = new FilePageFileStore(root);
            Expect(files.ReadCopy(0, false, false).Success && files.ReadCopy(1, false, false).NotFound,
                "earlier page must survive without the rolled-back page");
        });
    }

    private static void Storage_AsyncDurableLedgerFinalizesOnOwner()
    {
        WithStorageDirectory(root =>
        {
            using var store = new InventoryStore(new StoreConfig(), root);
            store.Pump();
            var ledgerPath = Path.Combine(root, StoreRedoJournal.LedgerName);
            Directory.CreateDirectory(ledgerPath);
            var bytes = new byte[] { 23 };
            var callbackThread = 0;
            var callbacks = 0;
            var durable = false;
            Expect(store.CommitTransactionLedgerAsync(bytes, (success, _) =>
            {
                callbacks++;
                callbackThread = Environment.CurrentManagedThreadId;
                durable = success;
            }, out var error), "ledger commit was rejected: " + error);
            bytes[0] = 99;
            Expect(!store.FlushForSave(out _), "save should report the blocked publication");
            Expect(callbacks == 1 && callbackThread == Environment.CurrentManagedThreadId && durable &&
                store.LoadTransactionLedger().SequenceEqual(new byte[] { 23 }), "redo marker must finalize as durable on the owner");
            Directory.Delete(ledgerPath);
            Expect(store.TryDispose(out error) && callbacks == 1 && File.ReadAllBytes(ledgerPath).SequenceEqual(new byte[] { 23 }),
                "close did not replay the immutable ledger: " + error);
        });
    }

    private static void Storage_PeriodicSaveSettlesOnOwner()
    {
        WithStorageDirectory(root =>
        {
            using var store = new InventoryStore(new StoreConfig(), root);
            store.Pump();
            var page = store.AllocatePage();
            store.MarkDirty(page);
            store.SchedulePeriodicSave();
            Expect(store.IsBusy && page.Dirty, "periodic save must leave finalization to the owner");
            Expect(store.FlushForSave(out var error), "owner flush could not settle the periodic save: " + error);
            Expect(!store.IsBusy && !page.Dirty && File.Exists(Path.Combine(root, FilePageFileStore.FileName(page.PageIndex))),
                "settled periodic save must persist the dirty page");
        });
    }

    private static void Storage_PeriodicSaveRefreshesMetadataThenSkipsCleanStore()
    {
        WithStorageDirectory(root =>
        {
            _ = new FilePageFileStore(root);
            File.WriteAllBytes(Path.Combine(root, FilePageFileStore.FileName(1)), FilePageFileStore.Snapshot(1, null, new PageSummary()));
            StoreMeta.Save(root, Array.Empty<ItemPage>()); // Valid metadata omits a page present on disk.
            using var store = new InventoryStore(new StoreConfig(), root);
            store.Pump();
            Expect(!store.PageCache.HasDirtyPages, "reconciliation must not dirty the page file");
            store.SchedulePeriodicSave();
            Expect(store.IsBusy, "periodic save must refresh incomplete metadata");
            Expect(store.FlushForSave(out var error), "metadata refresh did not settle: " + error);
            Expect(StoreMeta.Load(root, out var pages, out _) == StoreMetaStatus.Valid && pages.ContainsKey(1),
                "metadata did not include the reconciled page");

            var backup = Path.Combine(root, "store.meta.bak");
            var earlierMetadata = File.ReadAllBytes(backup);
            store.SchedulePeriodicSave();
            Expect(!store.IsBusy, "a clean periodic save must not schedule disk work");
            Expect(File.ReadAllBytes(backup).SequenceEqual(earlierMetadata), "a clean save rotated metadata backup");
        });
    }

    private static void Storage_AsyncPreparationPreservesUnsupportedPageCopies()
    {
        WithStorageDirectory(root =>
        {
            var files = new FilePageFileStore(root);
            var primary = Path.Combine(root, FilePageFileStore.FileName(0));
            var previous = FilePageFileStore.Snapshot(0, null, new PageSummary());
            var future = FilePageFileStore.Snapshot(0, null, new PageSummary());
            future[4] = 127;
            File.WriteAllBytes(primary, previous);
            File.WriteAllBytes(primary + ".bak", future);
            using var store = new InventoryStore(new StoreConfig(), root);
            store.Pump();
            var page = store.EnsurePage(0);
            store.MarkDirty(page);
            store.SchedulePeriodicSave();
            Expect(store.FlushForSave(out var error), "periodic flush failed: " + error);
            Expect(files.ReadCopy(0, false, false).Success && !File.ReadAllBytes(primary).SequenceEqual(previous),
                "async preparation failed to publish the new primary");
            Expect(File.ReadAllBytes(primary + ".bak").SequenceEqual(future), "unknown backup was overwritten");

            var callbacks = 0;
            var success = true;
            var command = CommandFactory.QueryType(12, 0, null);
            Expect(store.TryExecuteTransactionAsync(command, _ =>
            {
                File.WriteAllBytes(primary, future);
                store.MarkDirty(page);
                return new byte[] { 17 };
            }, (result, _) => { callbacks++; success = result; }, out error),
                "the owner did disk validation during transaction acceptance: " + error);
            Expect(SpinWait.SpinUntil(() => { store.Pump(); return callbacks > 0; }, 5000),
                "owner did not finalize async preparation failure");
            Expect(callbacks == 1 && !success && store.LoadTransactionLedger() is null && !store.PageCache.HasDirtyPages,
                "pre-marker failure did not roll back the transaction");
            Expect(File.ReadAllBytes(primary).SequenceEqual(future) && !Directory.Exists(Path.Combine(root, ".commit-pending")),
                "unsupported primary was modified or a marker was published");
        });
    }

    private static void Storage_AsyncPreparationRefusesUnsupportedMetadata()
    {
        foreach (var version in new[] { 1, 127 })
        {
            WithStorageDirectory(root =>
            {
                using var store = new InventoryStore(new StoreConfig(), root);
                store.Pump();
                var meta = Path.Combine(root, "store.meta");
                using (var stream = File.Create(meta))
                    TagIO.ToStream(new TagCompound { ["version"] = version }, stream, compress: true);
                var unsupported = File.ReadAllBytes(meta);
                Expect(StoreMeta.Load(root, out _, out _) == StoreMetaStatus.Invalid,
                    $"metadata v{version} did not fail to load");
                var callbacks = 0;
                string failure = null;
                Expect(store.CommitTransactionLedgerAsync(new byte[] { 17 }, (success, error) =>
                    { callbacks++; failure = success ? null : error; }, out var startError),
                    "the owner validated metadata during acceptance: " + startError);
                Expect(SpinWait.SpinUntil(() => { store.Pump(); return callbacks > 0; }, 5000),
                    "owner did not finalize metadata preparation failure");
                Expect(callbacks == 1 && failure?.Contains("unsupported store.meta", StringComparison.Ordinal) == true &&
                    !File.Exists(Path.Combine(root, StoreRedoJournal.LedgerName)) && File.ReadAllBytes(meta).SequenceEqual(unsupported),
                    $"metadata v{version} was not preserved on preparation failure");

                var backup = meta + ".bak";
                File.Move(meta, backup);
                StoreMeta.Save(root, Array.Empty<ItemPage>());
                StoreMeta.Save(root, Array.Empty<ItemPage>());
                Expect(StoreMeta.Load(root, out _, out _) == StoreMetaStatus.Valid && File.ReadAllBytes(backup).SequenceEqual(unsupported),
                    $"normal save must preserve the v{version} metadata backup");
            });
        }
    }

    private static void Storage_FailedDisposeRetainsDirtyPageForRetry()
    {
        WithStorageDirectory(root =>
        {
            using var store = new InventoryStore(new StoreConfig(), root);
            store.Pump();
            var page = store.AllocatePage();
            store.MarkDirty(page);
            var target = Path.Combine(root, FilePageFileStore.FileName(page.PageIndex));
            Directory.CreateDirectory(target);
            Expect(!store.TryDispose(out _) && !store.IsDisposed && page.Dirty,
                "a pre-commit save failure must leave the dirty store available for retry");
            Directory.Delete(target);
            Expect(store.TryDispose(out var error) && store.IsDisposed && !page.Dirty && File.Exists(target),
                "retry did not commit the retained page: " + error);
        });
    }

    private static void Storage_RejectsRemovedImportJournalTarget()
    {
        WithStorageDirectory(root =>
        {
            const string obsoleteMarker = "legacy-0.1-imported";
            var pending = Path.Combine(root, ".commit-pending");
            var journal = new StoreRedoJournal(root);
            Directory.CreateDirectory(pending);
            using (var writer = new BinaryWriter(File.Create(Path.Combine(pending, "manifest"))))
            {
                writer.Write(1);
                writer.Write(2);
                writer.Write(StoreRedoJournal.LedgerName);
                writer.Write(false);
                writer.Write(obsoleteMarker);
                writer.Write(false);
            }
            var ledger = Path.Combine(root, StoreRedoJournal.LedgerName);
            File.WriteAllBytes(ledger, new byte[] { 17 });
            var rejected = false;
            try { journal.Recover(); }
            catch (InvalidDataException) { rejected = true; }
            Expect(rejected && Directory.Exists(pending) && File.ReadAllBytes(ledger).SequenceEqual(new byte[] { 17 }),
                "obsolete target must be rejected before replay changes any live files");
        });
    }

    private static byte[] LegacyBlank(int index)
    {
        var payload = new byte[16 * sizeof(ulong) + 1024 * sizeof(int) + 3 * sizeof(ushort)];
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(0x4750494Cu);
        writer.Write((ushort)1);
        writer.Write((ushort)0);
        writer.Write(index);
        writer.Write(Crc32.Compute(payload, 0, payload.Length));
        writer.Write(payload.Length - sizeof(ushort));
        writer.Write(sizeof(ushort));
        writer.Write(payload);
        return stream.ToArray();
    }

    private static void WithStorageDirectory(Action<string> action)
    {
        var root = Path.Combine(Path.GetTempPath(), "LargerInventory.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try { action(root); }
        finally { Directory.Delete(root, recursive: true); }
    }
}
