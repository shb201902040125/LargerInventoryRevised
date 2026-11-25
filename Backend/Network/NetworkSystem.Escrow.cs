using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Terraria.ModLoader.IO;
using LargerInventory.Backend.Commands;
using Terraria;

namespace LargerInventory.Backend.Network;

public static partial class NetworkSystem
{
    private static readonly Dictionary<InventoryStore, long> EscrowStores = new();

    private static void RefreshEscrowLocks(InventoryStore store, TransactionLedger ledger)
    {
        var slots = new HashSet<int>();
        var lockedCounts = new Dictionary<int, long>();
        var entries = ledger.GetPrepared();
        var deadline = long.MaxValue;
        foreach (var (_, entry) in entries)
        {
            var record = EscrowRecord.Read(entry.Result);
            deadline = Math.Min(deadline, record.DeadlineUtcTicks);
            foreach (var (slot, original) in record.OriginalSlots)
            {
                slots.Add(slot);
                if (!original.IsAir)
                    lockedCounts[original.type] = lockedCounts.GetValueOrDefault(original.type) + original.stack;
            }
            if (!record.IsPut)
            {
                foreach (var item in record.Items)
                    lockedCounts[item.type] = lockedCounts.GetValueOrDefault(item.type) - item.stack;
            }
        }
        foreach (var type in lockedCounts.Where(pair => pair.Value <= 0).Select(pair => pair.Key).ToArray())
            lockedCounts.Remove(type);
        store.SetEscrowSlots(slots, lockedCounts);
        if (entries.Count == 0) EscrowStores.Remove(store);
        else EscrowStores[store] = deadline;
    }

    private static void ReplyPrepared(Session session, InventoryStore store, Scope scope, Guid correlation,
        Guid request, TransactionLedger.Entry entry)
    {
        var record = EscrowRecord.Read(entry.Result);
        if (record.Lineage != session.JournalLineage)
        {
            QueueResult(session, scope, correlation, request, 0, Encode(CommandResult.Fail(CommandStatus.PendingDelivery)));
            return;
        }
        if (record.DeadlineUtcTicks <= DateTime.UtcNow.Ticks)
        {
            ExpireEscrow(store, GetLedger(store), new(session.Player, request), entry, record,
                success =>
                {
                    if (!IsCurrent(session)) return;
                    if (success && GetLedger(store).TryGet(new(session.Player, request), out var expired))
                        ReplyEntry(session, store, scope, correlation, request, expired);
                    else
                        QueueResult(session, scope, correlation, request, 0, Encode(CommandResult.Fail(CommandStatus.PageBusy)));
                });
            return;
        }
        var preview = new ServerDelivery { OwnerLineage = record.Lineage, Status = record.Status,
            Remaining = record.IsPut ? new List<Item>() : record.Items };
        QueueResult(session, scope, correlation, request, PreparedFlag, preview.Write());
    }

    private static void ConsumeEscrow(Session session, InventoryStore store, Scope scope, Guid correlation,
        Guid request, long sequence)
    {
        var ledger = GetLedger(store);
        var key = new TransactionLedger.Key(session.Player, request);
        var lookup = ledger.Lookup(key, sequence > 0 ? sequence : null, out var entry);
        if (lookup != TransactionLedger.LookupStatus.Retained)
        {
            QueueResult(session, scope, correlation, request, 0, Encode(CommandResult.Fail(
                lookup == TransactionLedger.LookupStatus.Retired ? CommandStatus.Retired : CommandStatus.NotFound)));
            return;
        }
        if (!entry.Escrow)
        {
            ReplyEntry(session, store, scope, correlation, request, entry);
            return;
        }
        var record = EscrowRecord.Read(entry.Result);
        if (record.Lineage != session.JournalLineage || record.DeadlineUtcTicks <= DateTime.UtcNow.Ticks)
        {
            ReplyPrepared(session, store, scope, correlation, request, entry);
            return;
        }
        if (record.IsPut)
        {
            CommitEscrowPut(session, store, scope, correlation, key, ledger, entry, record);
            return;
        }
        void Reply(CommandStatus status) => QueueResult(session, scope, correlation, request, 0, Encode(CommandResult.Fail(status)));
        if (session.ConnectionPlayer.dead || session.CommittingTake || session.OfferedDeliveries.Count != 0)
        {
            Reply(CommandStatus.PendingDelivery);
            return;
        }
        TransactionLedger.Entry committed;
        byte[] contents;
        try
        {
            var delivery = new ServerDelivery { OwnerLineage = record.Lineage, Status = record.Status,
                Remaining = record.Items, KeepOverflow = true };
            var overflow = PlanDelivery(session.ConnectionPlayer, delivery);
            if (overflow.Count != 0 || delivery.Grants.Count == 0)
            {
                Reply(CommandStatus.PendingDelivery | CommandStatus.NoSpace);
                return;
            }
            delivery.Write(forClient: true);
            committed = entry with { Escrow = false, Deliver = true, Result = delivery.Write() };
            contents = ledger.Encode(key, committed);
        }
        catch (Exception error)
        {
            Log.Warn("Unable to prepare escrow delivery: " + error.Message);
            Reply(CommandStatus.PendingDelivery | CommandStatus.InternalError);
            return;
        }
        session.CommittingTake = true;
        if (!store.CommitTransactionLedgerAsync(contents, (success, error) =>
        {
            session.CommittingTake = false;
            if (success)
            {
                ledger.Publish(key, committed);
                RefreshEscrowLocks(store, ledger);
            }
            if (!IsCurrent(session)) return;
            if (success) ReplyEntry(session, store, scope, correlation, request, committed);
            else QueueResult(session, scope, correlation, request, 0, Encode(CommandResult.Fail(CommandStatus.PageSaveFailed)));
        }, out _))
        {
            session.CommittingTake = false;
            Reply(CommandStatus.PageBusy);
        }
    }

    private static void ExpireEscrow(InventoryStore store, TransactionLedger ledger, TransactionLedger.Key key,
        TransactionLedger.Entry entry, EscrowRecord record, Action<bool> completed = null)
    {
        var expired = entry with { Escrow = false, Result = Encode(CommandResult.Fail(CommandStatus.RequestTimeout | CommandStatus.RolledBack)) };
        var bytes = ledger.Encode(key, expired);
        void Finish(bool success, string error)
        {
            if (success)
            {
                ledger.Publish(key, expired);
                RefreshEscrowLocks(store, ledger);
            }
            completed?.Invoke(success);
        }
        var started = record.OriginalSlots.Count == 0
            ? store.CommitTransactionLedgerAsync(bytes, Finish, out _)
            : store.TryRestoreEscrowAsync(record.OriginalSlots, bytes, Finish, out _);
        if (!started) completed?.Invoke(false);
    }

    private static void PumpEscrowExpiry()
    {
        var now = DateTime.UtcNow.Ticks;
        foreach (var (store, deadline) in EscrowStores.ToArray())
        {
            if (deadline > now || store.IsBusy) continue;
            var ledger = GetLedger(store);
            foreach (var (key, entry) in ledger.GetPrepared())
            {
                var record = EscrowRecord.Read(entry.Result);
                if (record.DeadlineUtcTicks > now) continue;
                ExpireEscrow(store, ledger, key, entry, record);
                break;
            }
        }
    }

    // The issuer must keep the authorized source locked until Lookup reports a terminal result.
    public static bool AuthorizeWorldPutUpload(int slot, Guid request, byte[] expectedHash) =>
        AuthorizePutUpload(Scope.World, slot, request, expectedHash);

    public static bool AuthorizePlayerPutUpload(int slot, Guid request, byte[] expectedHash) =>
        AuthorizePutUpload(Scope.Player, slot, request, expectedHash);

    private static bool AuthorizePutUpload(Scope scope, int slot, Guid request, byte[] expectedHash)
    {
        if (request == Guid.Empty || IsRecoverableId(request) || expectedHash?.Length != 32 ||
            !Sessions.TryGetValue(slot, out var session) || !IsCurrent(session) ||
            session.PutUploads.Count >= RuntimeConfig.MaxPendingNetworkRequests || session.Sources.ContainsKey((scope, request)))
            return false;
        var store = scope == Scope.World ? session.WorldStore : session.PlayerStore;
        return store is not null && !GetLedger(store).TryGet(new(session.Player, request), out _) &&
            session.PutUploads.TryAdd((scope, request), (byte[])expectedHash.Clone());
    }

    private static void PrepareUploadedPut(Session session, InventoryStore store, Scope scope, Guid correlation,
        Guid request, long sequence, byte[] upload)
    {
        void Reply(CommandStatus status) => QueueResult(session, scope, correlation, request, 0,
            Encode(CommandResult.Fail(status)));
        if (sequence != 0 || IsRecoverableId(request) || upload is null || session.JournalLineage == Guid.Empty)
        {
            Reply(CommandStatus.InvalidArg);
            return;
        }
        // Separate the operation fingerprint from the hash used to authorize the bytes.
        var hash = SHA256.HashData(upload);
        var fingerprint = SHA256.HashData(new byte[] { (byte)Op.PreparePut }.Concat(hash).ToArray());
        var ledger = GetLedger(store);
        var key = new TransactionLedger.Key(session.Player, request);
        if (ledger.TryGet(key, out var retained))
        {
            if (retained.Matches(fingerprint)) ReplyEntry(session, store, scope, correlation, request, retained);
            else Reply(CommandStatus.InvalidArg);
            return;
        }
        if (!session.PutUploads.TryGetValue((scope, request), out var expected) ||
            !CryptographicOperations.FixedTimeEquals(expected, hash))
        {
            Reply(CommandStatus.InvalidArg);
            return;
        }
        if (!ledger.HasRoom)
        {
            Reply(CommandStatus.Throttled);
            return;
        }
        TransactionLedger.Entry entry;
        byte[] contents;
        try
        {
            using var stream = new MemoryStream(upload, writable: false);
            var tags = TagIO.FromStream(stream, compressed: false).GetList<TagCompound>("items");
            if (tags.Count == 0 || tags.Count > store.Config.MaxRemotePutStacks || stream.Position != stream.Length)
            {
                Reply(CommandStatus.InvalidArg);
                return;
            }
            var items = new List<Item>(tags.Count);
            foreach (var tag in tags) items.Add(ItemIO.Load(tag));
            if (!ValidateCommand(CommandFactory.Put(items), store.Config))
            {
                Reply(CommandStatus.InvalidArg);
                return;
            }
            var record = new EscrowRecord
            {
                DeadlineUtcTicks = DateTime.UtcNow.AddMilliseconds(store.Config.NetworkRequestTimeoutMs).Ticks,
                Lineage = session.JournalLineage,
                Items = items,
                IsPut = true,
                Spill = true
            };
            entry = new TransactionLedger.Entry(fingerprint, false, false, record.Write(), Escrow: true);
            contents = ledger.Encode(key, entry);
        }
        catch (Exception error)
        {
            Log.Warn("Unable to prepare uploaded Put: " + error.Message);
            Reply(CommandStatus.InvalidArg);
            return;
        }
        if (!store.CommitTransactionLedgerAsync(contents, (success, _) =>
        {
            if (success)
            {
                ledger.Publish(key, entry);
                RefreshEscrowLocks(store, ledger);
                session.PutUploads.Remove((scope, request));
            }
            if (!IsCurrent(session)) return;
            if (success) ReplyEntry(session, store, scope, correlation, request, entry);
            else Reply(CommandStatus.PageSaveFailed);
        }, out _)) Reply(CommandStatus.PageBusy);
    }

    private static void CommitEscrowPut(Session session, InventoryStore store, Scope scope, Guid correlation,
        TransactionLedger.Key key, TransactionLedger ledger, TransactionLedger.Entry entry, EscrowRecord record)
    {
        var command = record.Slots.Length == 0 ? CommandFactory.Put(record.Items, record.Spill) :
            CommandFactory.Put(record.Items, record.Slots);
        TransactionLedger.Entry final = null;
        if (!store.TryExecuteTransactionAsync(command, result =>
        {
            if ((result.Status & CommandStatus.InternalError) != 0)
                throw new InvalidOperationException("Escrow Put failed during commit.");
            final = entry with { Escrow = false, Result = Encode(new CommandResult(result.Status, CountItems(result.ResultPayload as List<Item>))) };
            return ledger.Encode(key, final);
        }, (success, _) =>
        {
            if (success)
            {
                ledger.Publish(key, final);
                RefreshEscrowLocks(store, ledger);
            }
            if (!IsCurrent(session)) return;
            if (success) ReplyEntry(session, store, scope, correlation, key.Request, final);
            else QueueResult(session, scope, correlation, key.Request, 0, Encode(CommandResult.Fail(CommandStatus.PageSaveFailed)));
        }, out _))
            QueueResult(session, scope, correlation, key.Request, 0, Encode(CommandResult.Fail(CommandStatus.PageBusy)));
    }
}
