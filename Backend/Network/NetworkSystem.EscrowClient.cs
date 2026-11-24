using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LargerInventory.Backend.Commands;
using LargerInventory.Backend.Integration;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader.IO;

namespace LargerInventory.Backend.Network;

public static partial class NetworkSystem
{
    /// <summary>An escrow offer. Items are display snapshots. Only Consume can commit the offer.</summary>
    public sealed class ItemEscrowPreview
    {
        private readonly List<Item> _items;

        internal ItemEscrowPreview(Guid operationId, CommandStatus status, List<Item> items)
        {
            OperationId = operationId;
            Status = status;
            _items = items;
        }

        public Guid OperationId { get; }
        public CommandStatus Status { get; }
        public bool IsPut => _items.Count == 0;
        public IReadOnlyList<Item> Items => _items.ConvertAll(item => item.Clone()).AsReadOnly();
    }

    private sealed class ClientEscrow(ItemEscrowPreview preview, long sequence)
    {
        public readonly ItemEscrowPreview Preview = preview;
        public readonly long Sequence = sequence;
        public bool Automatic;
        public long NextReadiness;
    }

    private static readonly Dictionary<(Scope Scope, Guid Request), ClientEscrow> ClientEscrows = new();
    private static readonly HashSet<(Scope Scope, Guid Request)> JournaledAutomaticEscrows = new();
    private static readonly Dictionary<(Scope Scope, Guid Request), Action<CommandResult>> AutomaticTakeCallbacks = new();

    /// <summary>Prepares a Take on the main thread and reports the preview through the callback. A new operation ID is journaled before sending.</summary>
    public static Guid PreparePlayerTake(Command command, Action<CommandResult> callback = null,
        Guid operationId = default, bool autoConsume = false) =>
        PrepareTake(Scope.Player, command, callback, operationId, autoConsume);

    /// <inheritdoc cref="PreparePlayerTake"/>
    public static Guid PrepareWorldTake(Command command, Action<CommandResult> callback = null,
        Guid operationId = default, bool autoConsume = false) =>
        PrepareTake(Scope.World, command, callback, operationId, autoConsume);

    private static Guid PrepareTake(Scope scope, Command command, Action<CommandResult> callback, Guid request,
        bool autoConsume, bool reportPreview = true)
    {
        ArgumentNullException.ThrowIfNull(command);
        callback ??= command.Callback;
        using var bytes = new MemoryStream();
        using var writer = new BinaryWriter(bytes);
        if (command.Kind != CommandKind.Take || !WriteCommand(writer, command, RuntimeConfig))
        {
            Invoke(callback, CommandResult.Fail(CommandStatus.InvalidArg));
            return request;
        }

        var recoverable = request == Guid.Empty;
        if (recoverable && !TryCreateClientOperationId(scope, recoverable: true, out request))
        {
            Invoke(callback, CommandResult.Fail(CommandStatus.Preparing));
            return request;
        }
        var sequence = 0L;
        if (!recoverable && IsRecoverableId(request) && !TryGetJournalSequence(scope, request, out sequence))
        {
            Invoke(callback, CommandResult.Fail(CommandStatus.InvalidArg));
            return request;
        }

        var payload = bytes.ToArray();
        var id = request;
        var sentId = StartRequest(scope, Op.Mutate, id, result =>
        {
            if (autoConsume && result.ResultPayload is ItemEscrowPreview { IsPut: false } &&
                ClientEscrows.TryGetValue((scope, id), out var escrow))
            {
                escrow.Automatic = true;
                if (!reportPreview)
                {
                    AutomaticTakeCallbacks[(scope, id)] = callback;
                    return;
                }
            }
            Invoke(callback, result);
        }, packet => packet.Write(payload), sequence: sequence, recoverable: recoverable);
        // Keep this session's opt-in if the first reply is lost and Lookup recovers the prepared entry.
        if (autoConsume && TryGetJournalSequence(scope, sentId, out _))
            JournaledAutomaticEscrows.Add((scope, sentId));
        return sentId;
    }

    /// <summary>Serializes a server-authorizable Put upload. Persist these exact bytes with the source reservation before sending.</summary>
    public static byte[] CreatePutUpload(IReadOnlyList<Item> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (items.Count == 0 || items.Count > RuntimeConfig.MaxRemotePutStacks)
            throw new ArgumentException("Invalid number of Put stacks.", nameof(items));
        var tags = new List<TagCompound>(items.Count);
        foreach (var item in items)
        {
            if (item is null || item.IsAir)
                throw new ArgumentException("A Put stack must contain an item.", nameof(items));
            tags.Add(ItemIO.Save(item));
        }
        using var stream = new MemoryStream();
        TagIO.ToStream(new TagCompound { ["items"] = tags }, stream, compress: false);
        if (stream.Length > MaxUploadBytes)
            throw new ArgumentException("Put upload exceeds the transport limit.", nameof(items));
        return stream.ToArray();
    }

    /// <summary>Prepares the exact server-authorized bytes of a persisted source reservation. Settle the source using the terminal result for this ID.</summary>
    public static Guid PreparePlayerPutUpload(Guid operationId, byte[] upload, Action<CommandResult> callback = null) =>
        PreparePutUpload(Scope.Player, operationId, upload, callback);

    /// <inheritdoc cref="PreparePlayerPutUpload"/>
    public static Guid PrepareWorldPutUpload(Guid operationId, byte[] upload, Action<CommandResult> callback = null) =>
        PreparePutUpload(Scope.World, operationId, upload, callback);

    private static Guid PreparePutUpload(Scope scope, Guid operationId, byte[] upload, Action<CommandResult> callback)
    {
        if (operationId == Guid.Empty || IsRecoverableId(operationId) || upload is null || upload.Length == 0 ||
            upload.Length > MaxUploadBytes)
        {
            Invoke(callback, CommandResult.Fail(CommandStatus.InvalidArg));
            return operationId;
        }
        var frozen = (byte[])upload.Clone();
        return StartRequest(scope, Op.PreparePut, operationId, callback, packet =>
        {
            packet.Write(frozen.Length);
            packet.Write(frozen);
        });
    }

    public static bool TryGetPlayerEscrowPreview(Guid operationId, out ItemEscrowPreview preview) =>
        TryGetEscrowPreview(Scope.Player, operationId, out preview);

    public static bool TryGetWorldEscrowPreview(Guid operationId, out ItemEscrowPreview preview) =>
        TryGetEscrowPreview(Scope.World, operationId, out preview);

    private static bool TryGetEscrowPreview(Scope scope, Guid operationId, out ItemEscrowPreview preview)
    {
        var found = ClientEscrows.TryGetValue((scope, operationId), out var escrow);
        preview = escrow?.Preview;
        return found;
    }

    /// <summary>Confirms a known offer. The caller declares readiness. Use Lookup first to recover an offer after reconnecting.</summary>
    public static Guid ConsumePlayerEscrow(Guid operationId, Action<CommandResult> callback = null) =>
        ConsumeEscrow(Scope.Player, operationId, callback);

    /// <inheritdoc cref="ConsumePlayerEscrow"/>
    public static Guid ConsumeWorldEscrow(Guid operationId, Action<CommandResult> callback = null) =>
        ConsumeEscrow(Scope.World, operationId, callback);

    private static Guid ConsumeEscrow(Scope scope, Guid operationId, Action<CommandResult> callback)
    {
        if (Main.netMode != NetmodeID.MultiplayerClient)
            throw new InvalidOperationException("Multiplayer client required.");
        if (!ClientEscrows.TryGetValue((scope, operationId), out var escrow))
        {
            Invoke(callback, CommandResult.Fail(CommandStatus.NotFound));
            return operationId;
        }
        if ((!escrow.Preview.IsPut && !CanReceiveEscrow()) || HasPendingEscrowRequest(scope, operationId))
        {
            Invoke(callback, CommandResult.Fail(CommandStatus.PendingDelivery));
            return operationId;
        }
        return StartRequest(scope, Op.Consume, operationId, callback, sequence: escrow.Sequence);
    }

    private static bool CanReceiveEscrow() => _client is not null && Main.LocalPlayer is { active: true, dead: false };

    private static bool HasPendingEscrowRequest(Scope scope, Guid request) =>
        Requests.Values.Any(pending => pending.Scope == scope && pending.Request == request);

    private static void HandleEscrowPrepared(Pending pending, byte[] bytes)
    {
        CommandResult result;
        try
        {
            var offer = ServerDelivery.Read(bytes);
            if (_client is null || offer.OwnerLineage != _client.JournalLineage || offer.Planned || offer.Generation != 0 ||
                offer.Grants.Count != 0 || offer.RecoveryItems.Count != 0 || offer.Remaining.Any(item => item is null || item.IsAir) ||
                offer.RemainingCount != CountItems(offer.Remaining) ||
                pending.Operation is not (Op.Mutate or Op.PreparePut or Op.Lookup or Op.Consume) ||
                pending.Operation == Op.Mutate && offer.Remaining.Count == 0 ||
                pending.Operation == Op.PreparePut && offer.Remaining.Count != 0)
                throw new InvalidDataException("Invalid warehouse escrow preview.");

            var key = (pending.Scope, pending.Request);
            var preview = new ItemEscrowPreview(pending.Request, offer.Status, offer.Remaining);
            var escrow = new ClientEscrow(preview, pending.Sequence)
            {
                Automatic = !preview.IsPut && JournaledAutomaticEscrows.Contains(key)
            };
            if (ClientEscrows.TryGetValue(key, out var previous) && previous.Sequence == pending.Sequence)
            {
                escrow.Automatic |= previous.Automatic;
                escrow.NextReadiness = previous.NextReadiness;
            }
            else if (ClientEscrows.Count >= Math.Max(2, RuntimeConfig.MaxPendingNetworkRequests))
            {
                // A caller can recover an evicted external reservation with Lookup. Keep journaled offers.
                var victim = ClientEscrows.FirstOrDefault(pair => pair.Value.Sequence == 0);
                if (victim.Key.Request != Guid.Empty)
                    ClientEscrows.Remove(victim.Key);
                else if (pending.Sequence == 0)
                {
                    Invoke(pending.Callback, CommandResult.Fail(CommandStatus.Throttled));
                    return;
                }
            }
            ClientEscrows[key] = escrow;
            result = new CommandResult(offer.Status | CommandStatus.PendingDelivery, preview);
        }
        catch (Exception error)
        {
            Log.Error("Warehouse escrow preview could not be read.", error);
            result = CommandResult.Fail(CommandStatus.InternalError);
        }
        Invoke(pending.Callback, result);
    }

    private static void ObserveEscrowClientResult(Pending pending, byte flags, CommandResult result)
    {
        var terminal = flags == CompletedOperationFlag && result.Status != CommandStatus.InternalError ||
            flags == 0 && (result.Status == CommandStatus.Retired ||
                result.Status == CommandStatus.NotFound && pending.Operation is Op.Lookup or Op.Consume ||
                (result.Status & CommandStatus.RolledBack) != 0);
        var key = (pending.Scope, pending.Request);
        if (terminal || flags == GrantFlag)
        {
            ClientEscrows.Remove(key);
            JournaledAutomaticEscrows.Remove(key);
        }
        if ((terminal || flags == GrantFlag && (result.Status & CommandStatus.PendingDelivery) == 0) &&
            AutomaticTakeCallbacks.Remove(key, out var callback))
            Invoke(callback, result);
    }

    private static void PumpEscrowClient(long now)
    {
        if (!CanReceiveEscrow() || Requests.Count >= RuntimeConfig.MaxPendingNetworkRequests)
            return;
        foreach (var (key, escrow) in ClientEscrows)
        {
            if (!escrow.Automatic || now < escrow.NextReadiness || HasPendingEscrowRequest(key.Scope, key.Request))
                continue;
            escrow.NextReadiness = now + RuntimeConfig.NetworkRecoveryRetryMs;
            try
            {
                // Readiness uses the delivery planner with clones. It never writes player slots.
                var inventory = (Item[])Main.LocalPlayer.inventory.Clone();
                var overflow = new List<Item>();
                PlayerItemDelivery.GiveToInventory(inventory, escrow.Preview.Items, overflow);
                if (overflow.Count == 0)
                    ConsumeEscrow(key.Scope, key.Request, null);
            }
            catch (Exception error)
            {
                Log.Error("Warehouse escrow readiness check failed.", error);
            }
            break;
        }
    }

    private static void ResetEscrowClient()
    {
        var callbacks = AutomaticTakeCallbacks.Values.ToArray();
        ClientEscrows.Clear();
        JournaledAutomaticEscrows.Clear();
        AutomaticTakeCallbacks.Clear();
        foreach (var callback in callbacks)
            Invoke(callback, CommandResult.Fail(CommandStatus.Cancelled));
    }
}
