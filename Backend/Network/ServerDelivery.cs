using System;
using System.Collections.Generic;
using System.IO;
using LargerInventory.Backend.Commands;
using Terraria;
using Terraria.ModLoader.IO;

namespace LargerInventory.Backend.Network;

internal sealed class ServerDelivery
{
    internal sealed record Grant(int Slot, byte[] Before, Item After);
    internal CommandStatus Status;
    internal bool Planned;
    internal bool KeepOverflow;
    // Zero denotes a delivery that has never offered inventory grants.
    internal long Generation { get; private set; }
    internal long AdvanceGeneration() => Generation = checked(Generation + 1);
    internal Guid OwnerLineage;

    internal bool MatchesGrant(Guid lineage, long generation) => Planned && Grants.Count != 0 &&
        lineage != Guid.Empty && OwnerLineage == lineage && generation > 0 && Generation == generation;

    internal int RemainingCount;
    internal List<Item> Remaining = new();
    // Original inputs for this generation. Kept server-side while the slot changes are unacknowledged.
    internal List<Item> RecoveryItems = new();
    internal List<Grant> Grants = new();

    internal static ServerDelivery Read(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        var tag = TagIO.FromStream(stream, false);
        if (!Guid.TryParseExact(tag.GetString("ownerLineage"), "N", out var lineage) || lineage == Guid.Empty)
        {
            throw new InvalidDataException("Invalid delivery owner lineage.");
        }
        var state = new ServerDelivery
        {
            Status = (CommandStatus)tag.GetInt("status"), Planned = tag.GetByte("planned") != 0,
            Generation = tag.GetLong("generation"),
            KeepOverflow = tag.GetByte("keepOverflow") != 0,
            OwnerLineage = lineage,
            RemainingCount = tag.GetInt("remainingCount"),
            Remaining = new List<Item>(tag.GetList<Item>("remaining")),
            RecoveryItems = new List<Item>(tag.GetList<Item>("recoveryItems"))
        };
        foreach (var grant in tag.GetList<TagCompound>("grants"))
        {
            state.Grants.Add(new Grant(grant.GetInt("slot"), grant.GetByteArray("before"), grant.Get<Item>("after")));
        }
        state.Validate();
        return state;
    }

    private void Validate()
    {
        if (OwnerLineage == Guid.Empty || Generation < 0 || Grants.Count != 0 && (!Planned || Generation == 0))
        {
            throw new InvalidDataException("Invalid delivery generation or owner.");
        }
    }

    internal byte[] Write(bool forClient = false)
    {
        Validate();
        var grants = new List<TagCompound>(Grants.Count);
        foreach (var grant in Grants)
        {
            grants.Add(new TagCompound { ["slot"] = grant.Slot, ["before"] = grant.Before, ["after"] = grant.After });
        }
        var tag = new TagCompound
        {
            ["status"] = (int)Status, ["planned"] = (byte)(Planned ? 1 : 0), ["grants"] = grants,
            ["remaining"] = forClient ? new List<Item>() : Remaining,
            ["remainingCount"] = NetworkSystem.CountItems(Remaining),
            ["generation"] = Generation,
            ["keepOverflow"] = (byte)(KeepOverflow ? 1 : 0),
            ["ownerLineage"] = OwnerLineage.ToString("N")
        };
        if (!forClient && RecoveryItems.Count != 0)
        {
            tag["recoveryItems"] = RecoveryItems;
        }
        using var stream = new MemoryStream();
        TagIO.ToStream(tag, stream, false);
        if (stream.Length > NetworkSystem.MaxResultBytes)
        {
            throw new InvalidDataException("Delivery plan exceeds the result size limit.");
        }
        return stream.ToArray();
    }

    internal static byte[] Snapshot(Item item)
    {
        if (item is null || item.IsAir)
        {
            return Array.Empty<byte>();
        }
        using var stream = new MemoryStream();
        TagIO.ToStream(ItemIO.Save(item), stream, false);
        return System.Security.Cryptography.SHA256.HashData(stream.GetBuffer().AsSpan(0, (int)stream.Length));
    }
}
