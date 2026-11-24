using System;
using System.Collections.Generic;
using System.IO;
using LargerInventory.Backend.Commands;
using Terraria;
using Terraria.ID;

namespace LargerInventory.Backend.Network;

public static partial class NetworkSystem
{
    public const int MaxEscrowConfirmationBatch = 32;
    internal readonly record struct EscrowConfirmation(Guid Correlation, Guid Request, long Sequence);
    private readonly record struct QueuedConfirmation(Scope Scope, Guid Lineage, EscrowConfirmation Entry, long Deadline);

    /// <summary>Confirms known world offers in one packet. Each operation receives its own result.</summary>
    public static void ConsumeWorldEscrows(IReadOnlyList<Guid> operationIds, Action<Guid, CommandResult> callback = null) =>
        SendConsumeBatch(Scope.World, operationIds, callback);

    /// <inheritdoc cref="ConsumeWorldEscrows"/>
    public static void ConsumePlayerEscrows(IReadOnlyList<Guid> operationIds, Action<Guid, CommandResult> callback = null) =>
        SendConsumeBatch(Scope.Player, operationIds, callback);

    private static void SendConsumeBatch(Scope scope, IReadOnlyList<Guid> operationIds, Action<Guid, CommandResult> callback)
    {
        ArgumentNullException.ThrowIfNull(operationIds);
        if (Main.netMode != NetmodeID.MultiplayerClient)
            throw new InvalidOperationException("Multiplayer client required.");
        if (operationIds.Count is < 1 or > MaxEscrowConfirmationBatch)
            throw new ArgumentOutOfRangeException(nameof(operationIds));

        var ids = new HashSet<Guid>();
        foreach (var request in operationIds)
            if (request == Guid.Empty || !ids.Add(request))
                throw new ArgumentException("Each operation ID must be nonempty and unique.", nameof(operationIds));

        var entries = new List<EscrowConfirmation>();
        var failures = new List<(Guid Request, CommandStatus Status)>();
        var session = _client;
        foreach (var request in operationIds)
        {
            CommandStatus failure;
            if (session is null)
                failure = CommandStatus.Preparing;
            else if (!ClientEscrows.TryGetValue((scope, request), out var escrow))
                failure = CommandStatus.NotFound;
            else if ((!escrow.Preview.IsPut && !CanReceiveEscrow()) || HasPendingEscrowRequest(scope, request))
                failure = CommandStatus.PendingDelivery;
            else if (Requests.Count >= RuntimeConfig.MaxPendingNetworkRequests)
                failure = CommandStatus.Throttled;
            else
            {
                var correlation = Guid.NewGuid();
                Requests.Add(correlation, new Pending { Scope = scope, Operation = Op.Consume, Request = request,
                    Sequence = escrow.Sequence, Started = Environment.TickCount64, Sent = true,
                    Callback = result => callback?.Invoke(request, result) });
                entries.Add(new EscrowConfirmation(correlation, request, escrow.Sequence));
                continue;
            }
            failures.Add((request, failure));
        }

        if (entries.Count != 0)
        {
            var packet = NewPacket(Op.ConsumeBatch);
            WriteGuid(packet, session.Token);
            packet.Write((byte)scope);
            WriteGuid(packet, session.JournalLineage);
            packet.Write((byte)entries.Count);
            foreach (var entry in entries)
            {
                WriteGuid(packet, entry.Correlation);
                WriteGuid(packet, entry.Request);
                packet.Write(entry.Sequence);
            }
            packet.Send();
        }
        foreach (var (request, status) in failures)
            Invoke(result => callback?.Invoke(request, result), CommandResult.Fail(status));
    }

    private static Action ReadConsumeBatch(BinaryReader reader, int slot)
    {
        var token = ReadGuid(reader);
        var scope = (Scope)reader.ReadByte();
        var lineage = ReadGuid(reader);
        var count = reader.ReadByte();
        if (scope is not (Scope.World or Scope.Player) || lineage == Guid.Empty || count is < 1 or > MaxEscrowConfirmationBatch)
            return null;

        var entries = ReadEscrowConfirmations(reader, count);
        if (entries is null) return null;
        return () =>
        {
            if (!Sessions.TryGetValue(slot, out var session) || session.Token != token || !IsCurrent(session))
                return;
            var deadline = Environment.TickCount64 + RuntimeConfig.NetworkRequestTimeoutMs;
            foreach (var entry in entries)
            {
                if (session.Confirmations.Count < RuntimeConfig.MaxPendingNetworkRequests)
                    session.Confirmations.Enqueue(new QueuedConfirmation(scope, lineage, entry, deadline));
                else
                    QueueResult(session, scope, entry.Correlation, entry.Request, 0, Encode(CommandResult.Fail(CommandStatus.Throttled)));
            }
        };
    }

    internal static EscrowConfirmation[] ReadEscrowConfirmations(BinaryReader reader, int count)
    {
        if (count is < 1 or > MaxEscrowConfirmationBatch) return null;
        var entries = new EscrowConfirmation[count];
        var requests = new HashSet<Guid>();
        var correlations = new HashSet<Guid>();
        for (var index = 0; index < count; index++)
        {
            var correlation = ReadGuid(reader);
            var request = ReadGuid(reader);
            var sequence = reader.ReadInt64();
            if (correlation == Guid.Empty || request == Guid.Empty || sequence < 0 ||
                !requests.Add(request) || !correlations.Add(correlation))
                return null;
            entries[index] = new EscrowConfirmation(correlation, request, sequence);
        }
        return entries;
    }

    private static void PumpConsumeBatches()
    {
        var now = Environment.TickCount64;
        foreach (var session in Sessions.Values)
        {
            for (var count = session.Confirmations.Count; count > 0; count--)
            {
                var confirmation = session.Confirmations.Dequeue();
                var store = confirmation.Scope == Scope.World ? session.WorldStore : session.PlayerStore;
                var entry = confirmation.Entry;
                if (now >= confirmation.Deadline)
                {
                    QueueResult(session, confirmation.Scope, entry.Correlation, entry.Request, 0,
                        Encode(CommandResult.Fail(CommandStatus.RequestTimeout)));
                    continue;
                }
                if (store is not null && (store.IsBusy ||
                    (session.OfferedDeliveries.Count != 0 && WaitsForDeliverySlot(session, store, entry.Request))))
                {
                    session.Confirmations.Enqueue(confirmation);
                    continue;
                }
                ProcessQueuedConfirmation(session, confirmation, entry);
                break;
            }
        }
    }

    private static bool WaitsForDeliverySlot(Session session, InventoryStore store, Guid request) =>
        GetLedger(store).TryGet(new(session.Player, request), out var entry) && entry.Escrow &&
        !EscrowRecord.Read(entry.Result).IsPut;

    private static void ProcessQueuedConfirmation(Session session, QueuedConfirmation confirmation, EscrowConfirmation entry)
    {
        try
        {
            HandleRequest(session, confirmation.Scope, entry.Correlation, entry.Request, Op.Consume, null,
                0, 0, entry.Sequence, confirmation.Lineage, 0, null);
        }
        catch (Exception error)
        {
            Log.Error("Warehouse confirmation failed.", error);
            QueueResult(session, confirmation.Scope, entry.Correlation, entry.Request, 0,
                Encode(CommandResult.Fail(CommandStatus.InternalError)));
        }
    }
}
