using System;
using System.Collections.Generic;
using LargerInventory.Backend.Commands;
using LargerInventory.Backend.Integration;
using LargerInventory.Backend.Storage;
using Terraria;

namespace LargerInventory.Backend.Network;

public static partial class NetworkSystem
{
    private static void ReleaseDelivery(Session session, InventoryStore store, TransactionLedger.Key key)
    {
        if (DeliveryOwners.TryGetValue((store, key), out var owner) && ReferenceEquals(owner, session))
            DeliveryOwners.Remove((store, key));
        session.OfferedDeliveries.Remove((store, key.Request));
        session.SentDeliveries.Remove((store, key.Request));
        // A control packet can settle a completed transfer while another lookup is still queued.
        for (var remaining = Outgoing.Count; remaining > 0 && Outgoing.TryDequeue(out var transfer); remaining--)
        {
            var target = transfer.Scope == Scope.World ? transfer.Session.WorldStore : transfer.Session.PlayerStore;
            if (ReferenceEquals(target, store) && transfer.Session.Player == key.Player && transfer.Request == key.Request &&
                transfer.Flags == GrantFlag)
                continue;
            Outgoing.Enqueue(transfer);
        }
    }

    private static void SaveDeliveryAsync(InventoryStore store, TransactionLedger ledger, TransactionLedger.Key key,
        TransactionLedger.Entry entry, ServerDelivery state, Action<bool> completed)
    {
        var updated = entry with { Result = state.Write(), Acknowledged = state.Planned && state.Grants.Count == 0 && state.Remaining.Count == 0 };
        if (store.CommitTransactionLedgerAsync(ledger.Encode(key, updated), (success, error) =>
            {
                if (success)
                    ledger.Publish(key, updated);
                else
                    Log.Warn("Server delivery state did not commit: " + error);
                completed(success);
            }, out var error))
            return;
        Log.Warn("Server delivery state could not start committing: " + error);
        completed(false);
    }

    private static bool OwnsSentDelivery(Session session, InventoryStore store, TransactionLedger.Key key, Guid correlation)
    {
        DeliveryOwners.TryGetValue((store, key), out var owner);
        session.SentDeliveries.TryGetValue((store, key.Request), out var sent);
        return CanAcknowledge(session.Token, owner?.Token ?? Guid.Empty, correlation, sent);
    }

    private static void RejectGrant(Session session, InventoryStore store, Guid request, Guid correlation, long generation)
    {
        var key = new TransactionLedger.Key(session.Player, request);
        if (generation <= 0 || !OwnsSentDelivery(session, store, key, correlation))
            return;
        var ledger = GetLedger(store);
        if (!ledger.TryGet(key, out var entry) || !entry.Deliver || entry.Acknowledged)
            return;
        var state = ServerDelivery.Read(entry.Result);
        if (!state.MatchesGrant(session.JournalLineage, generation) ||
            state.RecoveryItems.Count == 0)
            return;

        state.Remaining = state.RecoveryItems;
        state.RecoveryItems = new List<Item>();
        state.Grants.Clear();
        state.Planned = false;
        SaveDeliveryAsync(store, ledger, key, entry, state, success =>
        {
            if (success)
                ReleaseDelivery(session, store, key);
        });
    }

    private static void ReplyServerDelivery(Session session, InventoryStore store, Scope scope, Guid correlation, Guid request,
        TransactionLedger.Entry entry)
    {
        var ledger = GetLedger(store);
        var key = new TransactionLedger.Key(session.Player, request);
        var deliveryKey = (store, key);
        var player = session.ConnectionPlayer;
        if (!IsCurrent(session))
            return;
        void Reply(CommandResult result, bool completed = false) => QueueResult(session, scope, correlation, request,
            completed ? CompletedOperationFlag : (byte)0, Encode(result));
        void ReplyLatest(bool success)
        {
            if (!IsCurrent(session))
                return;
            if (success && ledger.TryGet(key, out var latest))
                ReplyServerDelivery(session, store, scope, correlation, request, latest);
            else
                Reply(CommandResult.Fail(CommandStatus.PendingDelivery | CommandStatus.PageSaveFailed));
        }

        if (PendingDropSaves.TryGetValue(deliveryKey, out var dropped))
        {
            SaveDeliveryAsync(store, ledger, key, entry, dropped, success =>
            {
                if (success)
                    PendingDropSaves.Remove(deliveryKey);
                ReplyLatest(success);
            });
            return;
        }

        var state = ServerDelivery.Read(entry.Result);
        if (entry.Acknowledged)
        {
            Reply(new CommandResult(state.Status, 0), completed: true);
            return;
        }
        if (state.OwnerLineage != session.JournalLineage || player.dead || (DeliveryOwners.TryGetValue(deliveryKey, out var previous) &&
            !ReferenceEquals(previous, session) && IsCurrent(previous)))
        {
            Reply(CommandResult.Fail(CommandStatus.PendingDelivery));
            return;
        }

        if (!state.Planned || (state.KeepOverflow && state.Grants.Count == 0 && state.Remaining.Count != 0))
        {
            if (session.OfferedDeliveries.Count != 0)
            {
                Reply(CommandResult.Fail(CommandStatus.PendingDelivery));
                return;
            }
            var remainingCount = CountItems(state.Remaining);
            var overflow = PlanDelivery(player, state);
            if (state.KeepOverflow && (state.Grants.Count == 0 || overflow.Count != 0))
            {
                Reply(new CommandResult(state.Status | CommandStatus.PendingDelivery | CommandStatus.NoSpace, remainingCount));
                return;
            }
            SaveDeliveryAsync(store, ledger, key, entry, state, ReplyLatest);
            return;
        }

        if (state.Remaining.Count != 0 && state.Grants.Count == 0)
        {
            // A failed write retries the post-drop snapshot, never the spawning operation.
            if (state.Remaining.RemoveAll(item => PlayerItemDelivery.TryDrop(player, item)) != 0)
            {
                PendingDropSaves[deliveryKey] = state;
                SaveDeliveryAsync(store, ledger, key, entry, state, success =>
                {
                    if (success)
                        PendingDropSaves.Remove(deliveryKey);
                    if (!IsCurrent(session))
                        return;
                    if (!success)
                        Reply(CommandResult.Fail(CommandStatus.PendingDelivery | CommandStatus.PageSaveFailed));
                    else if (state.Grants.Count != 0)
                        OfferGrant(session, store, scope, correlation, key, state);
                    else
                        Reply(new CommandResult(state.Remaining.Count == 0 ? state.Status :
                            state.Status | CommandStatus.PendingDelivery | CommandStatus.NoSpace, CountItems(state.Remaining)),
                            completed: state.Remaining.Count == 0);
                });
                return;
            }
        }
        if (state.Grants.Count != 0)
        {
            OfferGrant(session, store, scope, correlation, key, state);
            return;
        }
        Reply(new CommandResult(state.Status | CommandStatus.PendingDelivery | CommandStatus.NoSpace, CountItems(state.Remaining)));
    }

    private static List<Item> PlanDelivery(Player player, ServerDelivery state)
    {
        var inventory = (Item[])player.inventory.Clone();
        var overflow = new List<Item>();
        PlayerItemDelivery.GiveToInventory(inventory, state.Remaining.ConvertAll(item => item.Clone()), overflow);
        var end = Main.InventoryItemSlotsStart + Main.InventoryItemSlotsCount;
        for (var slot = Main.InventoryItemSlotsStart; slot < end; slot++)
        {
            if (!ReferenceEquals(inventory[slot], player.inventory[slot]))
                state.Grants.Add(new ServerDelivery.Grant(slot, ServerDelivery.Snapshot(player.inventory[slot]), inventory[slot]));
        }
        if (state.Grants.Count != 0)
        {
            state.AdvanceGeneration();
            state.RecoveryItems = state.Remaining;
        }
        state.Planned = true;
        state.Remaining = overflow;
        return overflow;
    }

    private static void OfferGrant(Session session, InventoryStore store, Scope scope, Guid correlation,
        TransactionLedger.Key key, ServerDelivery state)
    {
        if (Outgoing.Count >= RuntimeConfig.MaxNetworkTransfers || session.CommittingTake ||
            (session.OfferedDeliveries.Count != 0 && !session.OfferedDeliveries.Contains((store, key.Request))))
        {
            QueueResult(session, scope, correlation, key.Request, 0, Encode(CommandResult.Fail(CommandStatus.PendingDelivery)));
            return;
        }
        var bytes = state.Write(forClient: true);
        DeliveryOwners[(store, key)] = session;
        session.OfferedDeliveries.Add((store, key.Request));
        QueueResult(session, scope, correlation, key.Request, GrantFlag, bytes);
    }

    private static void Acknowledge(Session session, InventoryStore store, Guid request, Guid correlation, long generation)
    {
        if (store is null || store.IsBusy || _transactionRunning || generation <= 0)
            return;
        var ledger = GetLedger(store);
        var key = new TransactionLedger.Key(session.Player, request);
        if (!OwnsSentDelivery(session, store, key, correlation) || !ledger.TryGet(key, out var entry) ||
            !entry.Deliver || entry.Acknowledged)
            return;

        var state = ServerDelivery.Read(entry.Result);
        if (!state.MatchesGrant(session.JournalLineage, generation))
            return;

        state.Grants.Clear();
        state.RecoveryItems.Clear();
        SaveDeliveryAsync(store, ledger, key, entry, state, success =>
        {
            if (success)
            {
                ReleaseDelivery(session, store, key);
                // Settle overflow only after the received slots and their receipt are durable.
                if (state.Remaining.Count > 0 && IsCurrent(session) && ledger.TryGet(key, out var latest))
                    ReplyServerDelivery(session, store, ReferenceEquals(session.WorldStore, store) ? Scope.World : Scope.Player,
                        correlation, request, latest);
            }
        });
    }
}
