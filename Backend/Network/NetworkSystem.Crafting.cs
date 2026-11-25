using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using LargerInventory.Backend.Commands;
using LargerInventory.Backend.Crafting;

namespace LargerInventory.Backend.Network;

public static partial class NetworkSystem
{
    private const byte RecipeAvailabilityFlag = 64;
    private const int RecipeFingerprintBytes = 32; // SHA-256
    private const int RecipeResultHeaderBytes = sizeof(int) * 2;
    private const int MaxRecipeResultBytes = 64 * 1024;
    private const int MaxWireRecipes = (MaxRecipeResultBytes - RecipeResultHeaderBytes) * 8;
    private const int RecipeQueryIntervalMs = 250;
    private const int MaxConcurrentRecipeQueries = 4;

    private static readonly HashSet<Session> ActiveRecipeQueries = new();
    private static RecipeCatalog _helloRecipeCatalog;
    private static bool _sentRecipeHello;

    private readonly record struct RecipeTableStamp(int Count, byte[] Fingerprint);

    /// <summary>Asks the multiplayer server for the recipes currently available from the selected warehouse.</summary>
    /// <remarks>Call on the game thread. Successful results contain a <see cref="CraftingAvailability"/>.</remarks>
    public static Guid RequestAvailableRecipes(bool world, Action<CommandResult> callback)
    {
        if (_client is { } session && !HasCalibratedRecipes(session))
        {
            Invoke(callback, CommandResult.Fail(CommandStatus.RecipeTableMismatch));
            return Guid.Empty;
        }

        foreach (var pending in Requests.Values)
        {
            if (pending.Operation == Op.AvailableRecipes)
            {
                Invoke(callback, CommandResult.Fail(CommandStatus.Throttled));
                return Guid.Empty;
            }
        }

        return StartRequest(world ? Scope.World : Scope.Player, Op.AvailableRecipes, default, callback);
    }

    private static void WriteRecipeTable(BinaryWriter writer, RecipeCatalog catalog)
    {
        var fingerprint = catalog?.Fingerprint;
        if (fingerprint?.Length != RecipeFingerprintBytes || catalog.Count is < 0 or > MaxWireRecipes)
        {
            writer.Write(-1);
            return;
        }

        writer.Write(catalog.Count);
        writer.Write(fingerprint);
    }

    private static RecipeTableStamp ReadRecipeTable(BinaryReader reader)
    {
        var count = reader.ReadInt32();
        if (count == -1)
            return new RecipeTableStamp(-1, null);
        if (count is < 0 or > MaxWireRecipes)
            throw new InvalidDataException("Invalid recipe table size.");

        var fingerprint = reader.ReadBytes(RecipeFingerprintBytes);
        if (fingerprint.Length != RecipeFingerprintBytes)
            throw new EndOfStreamException();
        return new RecipeTableStamp(count, fingerprint);
    }

    private static bool RecipeTableMatches(RecipeCatalog catalog, RecipeTableStamp peer) =>
        catalog is not null && catalog.Count == peer.Count &&
        catalog.Fingerprint is { Length: RecipeFingerprintBytes } fingerprint &&
        fingerprint.AsSpan().SequenceEqual(peer.Fingerprint);

    private static bool HasCalibratedRecipes(Session session) =>
        session.CalibratedRecipes is { } catalog && ReferenceEquals(catalog, CraftingQueries.Catalog);

    private static void HandleRecipeQuery(Session session, Scope scope, Guid correlation, Guid request)
    {
        if (Outgoing.Count >= RuntimeConfig.MaxNetworkTransfers)
            return;

        void Reply(CommandResult result) => QueueResult(session, scope, correlation, request,
            RecipeAvailabilityFlag, EncodeRecipeAvailability(result, session.CalibratedRecipes));

        var now = Environment.TickCount64;
        if (now < session.NextRecipeQuery || ActiveRecipeQueries.Contains(session) ||
            ActiveRecipeQueries.Count >= Math.Min(MaxConcurrentRecipeQueries, RuntimeConfig.MaxNetworkTransfers))
        {
            // Rate-limit the rejection traffic as well as the work.
            if (now >= session.NextRecipeThrottleReply)
            {
                session.NextRecipeThrottleReply = now + RecipeQueryIntervalMs;
                Reply(CommandResult.Fail(CommandStatus.Throttled));
            }
            return;
        }
        session.NextRecipeQuery = now + RecipeQueryIntervalMs;

        if (!HasCalibratedRecipes(session))
        {
            Reply(CommandResult.Fail(CommandStatus.RecipeTableMismatch));
            return;
        }

        var store = scope == Scope.World ? session.WorldStore : session.PlayerStore;
        if (store is null)
        {
            Reply(CommandResult.Fail(CommandStatus.InternalError));
            return;
        }

        ActiveRecipeQueries.Add(session);
        void Complete(CommandResult result)
        {
            if (!ActiveRecipeQueries.Remove(session) || !IsCurrent(session))
                return;
            if (Outgoing.Count < RuntimeConfig.MaxNetworkTransfers)
                Reply(HasCalibratedRecipes(session) ? result : CommandResult.Fail(CommandStatus.RecipeTableMismatch));
        }

        try
        {
            CraftingQueries.Query(store, Complete, session.ConnectionPlayer);
        }
        catch (Exception e)
        {
            Log.Error("Warehouse recipe query failed.", e);
            Complete(CommandResult.Fail(CommandStatus.InternalError));
        }
    }

    private static byte[] EncodeRecipeAvailability(CommandResult result, RecipeCatalog catalog)
    {
        var status = result.Status;
        var count = 0;
        byte[] bitmap = null;
        if (status == CommandStatus.Ok)
        {
            if (result.ResultPayload is CraftingAvailability availability && availability.RecipeCount == catalog?.Count &&
                availability.RecipeCount is >= 0 and <= MaxWireRecipes)
            {
                count = availability.RecipeCount;
                bitmap = availability.CopyBitmap();
            }

            if (bitmap is null || !ValidRecipeBitmap(count, bitmap))
            {
                status = CommandStatus.InternalError;
                count = 0;
                bitmap = null;
            }
        }

        var bytes = new byte[RecipeResultHeaderBytes + (bitmap?.Length ?? 0)];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, (int)status);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(sizeof(int)), count);
        bitmap?.CopyTo(bytes, RecipeResultHeaderBytes);
        return bytes;
    }

    private static CommandResult DecodeRecipeAvailability(Session session, byte[] bytes)
    {
        if (!HasCalibratedRecipes(session))
            return CommandResult.Fail(CommandStatus.RecipeTableMismatch);
        if (bytes.Length < RecipeResultHeaderBytes)
            return CommandResult.Fail(CommandStatus.InternalError);

        var status = (CommandStatus)BinaryPrimitives.ReadInt32LittleEndian(bytes);
        var count = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(sizeof(int)));
        if (status != CommandStatus.Ok)
            return CommandResult.Fail(count == 0 && bytes.Length == RecipeResultHeaderBytes ? status : CommandStatus.InternalError);
        if (count != session.CalibratedRecipes.Count)
            return CommandResult.Fail(CommandStatus.RecipeTableMismatch);

        var bitmap = bytes.AsSpan(RecipeResultHeaderBytes);
        return ValidRecipeBitmap(count, bitmap)
            ? CommandResult.Ok(new CraftingAvailability(count, bitmap.ToArray()))
            : CommandResult.Fail(CommandStatus.InternalError);
    }

    internal static bool ValidRecipeBitmap(int count, ReadOnlySpan<byte> bitmap)
    {
        if (count is < 0 or > MaxWireRecipes || bitmap.Length != (count + 7) / 8)
            return false;
        var usedBits = count % 8;
        return usedBits == 0 || bitmap[^1] >> usedBits == 0;
    }
}
