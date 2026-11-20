using System;
using System.Collections.Generic;
using System.IO;
using LargerInventory.Backend.Commands;
using LargerInventory.Backend.Storage;
using Terraria;

namespace LargerInventory.Backend.Network;

public static partial class NetworkSystem
{
    // The UUID namespace distinguishes compactable operation IDs from the GUID-only API.
    private static ReadOnlySpan<byte> RecoveryNamespace => "LIORDv6!"u8;

    private static Guid NewRecoverableId()
    {
        var bytes = Guid.NewGuid().ToByteArray();
        RecoveryNamespace.CopyTo(bytes);
        return new Guid(bytes);
    }

    private static bool IsRecoverableId(Guid id) => id.ToByteArray().AsSpan(0, 8).SequenceEqual(RecoveryNamespace);

    private static bool HasPendingTake(Session session, InventoryStore store, Guid request)
    {
        var other = ReferenceEquals(store, session.WorldStore) ? session.PlayerStore : session.WorldStore;
        return GetLedger(store).HasPendingDelivery(session.Player, request) ||
            other is not null && (other.IsBusy || GetLedger(other).HasPendingDelivery(session.Player));
    }

    private static void ExecuteAsync(Session session, InventoryStore store, Guid request, long sequence,
        byte[] fingerprint, Func<Command> create, Action<TransactionLedger.Entry, CommandResult> completed)
    {
        var ledger = GetLedger(store);
        if (!ledger.HasRoom)
        {
            completed(null, CommandResult.Fail(CommandStatus.Throttled));
            return;
        }

        var key = new TransactionLedger.Key(session.Player, request);
        Command reserved = null;
        List<Item> originalItems = null;
        TransactionLedger.Entry entry = null;
        CommandResult committedResult = default;
        string error;
        try
        {
            reserved = create();
            if (!ValidateCommand(reserved, store.Config) || reserved.Kind is not (CommandKind.Put or CommandKind.Move or CommandKind.Take))
            {
                completed(null, reserved is null ? CommandResult.Fail(CommandStatus.InvalidArg) :
                    Command.FailUnexecuted(reserved, CommandStatus.InvalidArg));
                return;
            }

            object payload = reserved.InputPayload;
            if (payload is PutPayload put)
            {
                originalItems = new List<Item>(put.Items);
                payload = new PutPayload { Items = originalItems.ConvertAll(item => item.Clone()), Spill = put.Spill };
            }

            var executable = new Command(reserved.Kind, 0, reserved.Access, payload);
            if (store.TryExecuteTransactionAsync(executable, result =>
                {
                    if ((result.Status & (CommandStatus.InternalError | CommandStatus.PageSaveFailed)) != 0)
                        throw new InvalidDataException("Command did not complete safely.");
                    var delivery = executable.Kind == CommandKind.Take && result.ResultPayload is List<Item> { Count: > 0 };
                    committedResult = result;
                    var encoded = delivery
                        ? new ServerDelivery
                        {
                            Status = result.Status,
                            Remaining = (List<Item>)result.ResultPayload,
                            OwnerLineage = session.JournalLineage
                        }.Write()
                        : Encode(executable.Kind == CommandKind.Put
                            ? new CommandResult(result.Status, CountItems(result.ResultPayload as List<Item>)) : result);
                    entry = new TransactionLedger.Entry(fingerprint, delivery, false, encoded,
                        Sequence: sequence > 0 ? sequence : null);
                    return ledger.Encode(key, entry);
                }, (success, failure) =>
                {
                    if (!success)
                    {
                        Log.Warn("Warehouse transaction did not commit: " + failure);
                        completed(null, Command.FailUnexecuted(reserved, CommandStatus.PageSaveFailed));
                        return;
                    }
                    ledger.Publish(key, entry);
                    completed(entry, originalItems is not null && committedResult.ResultPayload is List<Item> { Count: > 0 }
                        ? new CommandResult(committedResult.Status, originalItems) : committedResult);
                }, out error))
                return;
        }
        catch (Exception ex)
        {
            Log.Error("Warehouse transaction failed.", ex);
            error = ex.Message;
        }

        Log.Warn("Warehouse transaction could not start: " + error);
        completed(null, reserved is null ? CommandResult.Fail(CommandStatus.PageSaveFailed) :
            Command.FailUnexecuted(reserved, CommandStatus.PageSaveFailed));
    }

    private static void RetireOperation(Session session, InventoryStore store, Guid request, long sequence,
        Action<CommandResult> reply)
    {
        if (sequence <= 0 || session.JournalLineage == Guid.Empty || !IsRecoverableId(request))
        {
            reply(CommandResult.Fail(CommandStatus.InvalidArg));
            return;
        }

        var ledger = GetLedger(store);
        var key = new TransactionLedger.Key(session.Player, request);
        var status = ledger.Lookup(key, sequence, out var entry);
        if (status == TransactionLedger.LookupStatus.Retired)
        {
            reply(CommandResult.Fail(CommandStatus.Retired));
            return;
        }
        if (status != TransactionLedger.LookupStatus.Retained)
        {
            reply(CommandResult.Fail(CommandStatus.NotFound));
            return;
        }
        if (entry.Deliver && !entry.Acknowledged || !ledger.TryPrepareCompaction(session.Player, sequence, out var compaction))
        {
            reply(CommandResult.Fail(CommandStatus.PendingDelivery));
            return;
        }

        if (!store.CommitTransactionLedgerAsync(compaction.Contents, (success, error) =>
        {
            if (success)
                ledger.Publish(compaction);
            else
                Log.Warn("Warehouse compaction failed: " + error);
            if (IsCurrent(session))
                reply(CommandResult.Fail(success ? CommandStatus.Retired : CommandStatus.PageSaveFailed));
        }, out var beginError))
        {
            Log.Warn("Warehouse compaction could not start: " + beginError);
            reply(CommandResult.Fail(CommandStatus.PageSaveFailed));
        }
    }
}
