using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using LargerInventory.Backend.Network;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader.IO;
using Terraria.Utilities;

namespace LargerInventory.Backend.Integration;

internal enum ServerGrantOutcome
{
    Applied,
    Unavailable,
    Conflict,
    DefinitelyUnapplied
}

public static class InventoryClientState
{
    private const string ReceiptTag = "WarehouseReceipts";
    // Positive values fence rejected generations. Negative values fence uncertain application after a crash.
    private const string GrantGenerationPrefix = "g/";
    private const int ReceiptKeyLength = 67;
    private static int MaxReceiptCount => StoreConfig.Runtime.MaxClientDeliveryReceipts;

    internal static void LoadPlayerReceipts(TagCompound tag, Dictionary<string, byte[]> destination)
    {
        destination.Clear();
        var receipts = tag.GetCompound(ReceiptTag);
        foreach (var pair in receipts)
        {
            if (pair.Value is byte[] bytes &&
                ((pair.Key.Length == ReceiptKeyLength && bytes.Length == 0 &&
                  TryReadGrantMarker(receipts.GetByteArray(GrantGenerationPrefix + pair.Key), out var generation) && generation < 0) ||
                 (pair.Key.Length == GrantGenerationPrefix.Length + ReceiptKeyLength &&
                  pair.Key.StartsWith(GrantGenerationPrefix, StringComparison.Ordinal))))
            {
                destination.Add(pair.Key, bytes);
            }
        }
    }

    internal static void SavePlayerReceipts(TagCompound tag, Dictionary<string, byte[]> source)
    {
        var receipts = new TagCompound();
        lock (source)
        {
            foreach (var pair in source)
            {
                receipts[pair.Key] = pair.Value;
            }
        }

        tag[ReceiptTag] = receipts;
    }

    // Run on the game thread. The transport authorizes a positive generation only on its owner save lineage.
    internal static ServerGrantOutcome ApplyServerGrant(Player player, string key, ServerDelivery delivery,
        Guid lineage)
    {
        var file = Main.ActivePlayerFileData;
        if (player is null || player.dead || file?.Player != player || string.IsNullOrWhiteSpace(file.Path))
        {
            return ServerGrantOutcome.Unavailable;
        }

        if (!delivery.MatchesGrant(lineage, delivery.Generation))
        {
            return ServerGrantOutcome.Conflict;
        }

        var receiver = player.GetModPlayer<InventoryPlayer>();
        var receipts = receiver.DeliveryReceipts;
        var generation = delivery.Generation;
        var markerKey = GrantGenerationPrefix + key;
        receipts.TryGetValue(key, out var receipt);
        receipts.TryGetValue(markerKey, out var marker);
        if (!TryReadGrantMarker(marker, out var markedGeneration) ||
            (markedGeneration > 0 && generation < markedGeneration))
        {
            return ServerGrantOutcome.Conflict;
        }

        try
        {
            // An existing receipt takes precedence over inventory snapshots. A receipt from a failed save
            // still forbids rejection and any further inventory writes until the existing save is retried.
            if (receipt is not null)
            {
                if (receipt.Length != 0 || markedGeneration != -generation)
                {
                    return ServerGrantOutcome.Conflict;
                }

                Player.SavePlayer(file, skipMapSave: true);
                return IsReceiptSaved(file.Path, file.IsCloudSave, receiver, key, receipt)
                    ? ServerGrantOutcome.Applied : ServerGrantOutcome.Unavailable;
            }

            // A replaced or stale loaded character may be missing an on-disk receipt or rejection.
            // A missing save cannot establish that this generation is safe to use.
            var saved = ReadSavedReceipts(file.Path, file.IsCloudSave, receiver);
            if (saved is null)
            {
                return ServerGrantOutcome.Unavailable;
            }
            if (saved.ContainsKey(key) || (saved.ContainsKey(markerKey) &&
                (!TryReadGrantMarker(saved.GetByteArray(markerKey), out var savedGeneration) ||
                 savedGeneration < 0 || savedGeneration > markedGeneration)))
            {
                return ServerGrantOutcome.Conflict;
            }

            if (markedGeneration < 0)
            {
                // The vanilla player save may have committed slot writes before the mod receipt committed.
                return ServerGrantOutcome.Conflict;
            }
            if (generation == markedGeneration)
            {
                return SaveGrantRejection(player, key, markerKey, marker);
            }

            var seen = new HashSet<int>();
            var first = Main.InventoryItemSlotsStart;
            var last = Math.Min(first + Main.InventoryItemSlotsCount, player.inventory.Length);
            foreach (var grant in delivery.Grants)
            {
                if (grant.Slot < first || grant.Slot >= last || grant.After is null || grant.After.IsAir ||
                    !seen.Add(grant.Slot) || grant.Before is not { Length: 0 or 32 })
                {
                    return ServerGrantOutcome.Conflict;
                }
            }

            foreach (var grant in delivery.Grants)
            {
                if (grant.Before.AsSpan().SequenceEqual(ServerDelivery.Snapshot(player.inventory[grant.Slot])))
                {
                    continue;
                }
                if (marker is null && receipts.Count >= MaxReceiptCount)
                {
                    return ServerGrantOutcome.Unavailable;
                }

                marker = WriteGrantMarker(receipts, markerKey, generation);
                return SaveGrantRejection(player, key, markerKey, marker);
            }

            var entriesNeeded = marker is null ? 2 : 1;
            if (receipts.Count > MaxReceiptCount - entriesNeeded)
            {
                return ServerGrantOutcome.Unavailable;
            }

            // Save the intent first. A crash between the vanilla and mod saves must leave this
            // generation fenced even if the final receipt never makes it to disk.
            var intent = WriteGrantMarker(receipts, markerKey, -generation);
            var intentVerified = false;
            try
            {
                Player.SavePlayer(file, skipMapSave: true);
                intentVerified = IsReceiptSaved(file.Path, file.IsCloudSave, receiver, markerKey, intent);
                if (!intentVerified)
                {
                    return ServerGrantOutcome.Unavailable;
                }
            }
            finally
            {
                // The intent stays in memory once verified; all subsequent paths may write slots.
                // On an inconclusive save, a retry can only proceed if the disk has no intent.
                if (!intentVerified)
                {
                    lock (receipts)
                    {
                        if (marker is null)
                        {
                            receipts.Remove(markerKey);
                        }
                        else
                        {
                            receipts[markerKey] = marker;
                        }
                    }
                }
            }

            lock (receipts)
            {
                foreach (var grant in delivery.Grants)
                {
                    player.inventory[grant.Slot] = grant.After;
                }
                receipt = Array.Empty<byte>();
                receipts.Add(key, receipt);
            }

            foreach (var grant in delivery.Grants)
            {
                NetMessage.SendData(MessageID.SyncEquipment, number: player.whoAmI, number2: grant.Slot);
            }

            Player.SavePlayer(file, skipMapSave: true);
            return IsReceiptSaved(file.Path, file.IsCloudSave, receiver, key, receipt)
                ? ServerGrantOutcome.Applied : ServerGrantOutcome.Unavailable;
        }
        catch (Exception error)
        {
            Log.Error("Could not process the warehouse grant or verify its saved receipt.", error);
            return ServerGrantOutcome.Unavailable;
        }
    }

    private static byte[] WriteGrantMarker(Dictionary<string, byte[]> receipts, string key, long generation)
    {
        var marker = new byte[sizeof(long)];
        BinaryPrimitives.WriteInt64LittleEndian(marker, generation);
        lock (receipts)
        {
            receipts[key] = marker;
        }
        return marker;
    }

    private static bool TryReadGrantMarker(byte[] marker, out long generation)
    {
        generation = 0;
        if (marker is null)
        {
            return true;
        }
        if (marker.Length != sizeof(long))
        {
            return false;
        }
        generation = BinaryPrimitives.ReadInt64LittleEndian(marker);
        return generation != 0 && generation != long.MinValue;
    }

    private static ServerGrantOutcome SaveGrantRejection(Player player, string key, string markerKey, byte[] marker)
    {
        var file = Main.ActivePlayerFileData;
        Player.SavePlayer(file, skipMapSave: true);
        var saved = ReadSavedReceipts(file.Path, file.IsCloudSave, player.GetModPlayer<InventoryPlayer>());
        if (saved is null || !saved.ContainsKey(markerKey) || !marker.AsSpan().SequenceEqual(saved.GetByteArray(markerKey)))
        {
            return ServerGrantOutcome.Unavailable;
        }
        return saved.ContainsKey(key) ? ServerGrantOutcome.Conflict : ServerGrantOutcome.DefinitelyUnapplied;
    }

    // The server must have durably retired the operation before removing its local replay fences.
    internal static void ForgetRetiredReceipt(Player player, string key)
    {
        var file = Main.ActivePlayerFileData;
        if (player is null || file?.Player != player || string.IsNullOrWhiteSpace(file.Path))
        {
            return;
        }

        var receipts = player.GetModPlayer<InventoryPlayer>().DeliveryReceipts;
        bool changed;
        lock (receipts)
        {
            changed = receipts.Remove(key);
            changed |= receipts.Remove(GrantGenerationPrefix + key);
        }
        if (!changed)
        {
            return;
        }
        try
        {
            Player.SavePlayer(file, skipMapSave: true);
        }
        catch (Exception error)
        {
            // A failed cleanup can leave an obsolete receipt on disk until the next player save.
            Log.Error("Could not save retired warehouse receipt cleanup.", error);
        }
    }

    private static bool IsReceiptSaved(string path, bool cloud, InventoryPlayer receiver, string key, byte[] expected)
    {
        var receipts = ReadSavedReceipts(path, cloud, receiver);
        return receipts is not null && receipts.ContainsKey(key) &&
            expected.AsSpan().SequenceEqual(receipts.GetByteArray(key));
    }

    private static TagCompound ReadSavedReceipts(string path, bool cloud, InventoryPlayer receiver)
    {
        var modFile = Path.ChangeExtension(path, ".tplr");
        if (!FileUtilities.Exists(modFile, cloud))
        {
            return null;
        }

        using var stream = new MemoryStream(FileUtilities.ReadAllBytes(modFile, cloud), writable: false);
        var root = TagIO.FromStream(stream);
        foreach (var entry in root.GetList<TagCompound>("modData"))
        {
            if (entry.GetString("mod") == receiver.Mod.Name && entry.GetString("name") == nameof(InventoryPlayer))
            {
                return entry.GetCompound("data").GetCompound(ReceiptTag);
            }
        }

        return null;
    }
}
