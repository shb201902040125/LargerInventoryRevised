using System;
using System.Collections.Generic;
using System.IO;
using LargerInventory.Backend.Commands;
using Terraria;
using Terraria.ModLoader.IO;

namespace LargerInventory.Backend.Network;

/// <summary>Durable inputs and rollback state for a prepared store transaction.</summary>
internal sealed class EscrowRecord
{
    internal long DeadlineUtcTicks { get; init; }
    internal Guid Lineage { get; init; }
    internal CommandStatus Status { get; init; }
    internal IReadOnlyDictionary<int, Item> OriginalSlots { get; init; } = new Dictionary<int, Item>();
    internal List<Item> Items { get; init; } = new();
    internal bool IsPut { get; init; }
    internal bool Spill { get; init; }
    internal int[] Slots { get; init; } = Array.Empty<int>();

    internal byte[] Write()
    {
        Validate();
        var originals = new List<TagCompound>(OriginalSlots.Count);
        foreach (var (slot, item) in OriginalSlots)
            originals.Add(new TagCompound { ["slot"] = slot, ["item"] = item });

        var tag = new TagCompound
        {
            ["deadline"] = DeadlineUtcTicks,
            ["lineage"] = Lineage.ToString("N"),
            ["status"] = (int)Status,
            ["originals"] = originals,
            ["items"] = Items,
            ["put"] = (byte)(IsPut ? 1 : 0),
            ["spill"] = (byte)(Spill ? 1 : 0),
            ["slots"] = Slots
        };
        using var stream = new MemoryStream();
        TagIO.ToStream(tag, stream, false);
        if (stream.Length > NetworkSystem.MaxResultBytes)
            throw new InvalidDataException("Prepared escrow exceeds the result size limit.");
        return stream.ToArray();
    }

    internal static EscrowRecord Read(byte[] bytes)
    {
        if (bytes is null || bytes.Length == 0 || bytes.Length > NetworkSystem.MaxResultBytes)
            throw new InvalidDataException("Invalid prepared escrow size.");

        using var stream = new MemoryStream(bytes, writable: false);
        var tag = TagIO.FromStream(stream, false);
        if (stream.Position != stream.Length || !Guid.TryParseExact(tag.GetString("lineage"), "N", out var lineage) ||
            !tag.ContainsKey("originals") || !tag.ContainsKey("items") || !tag.ContainsKey("slots") ||
            !tag.ContainsKey("status") || !tag.ContainsKey("put") || !tag.ContainsKey("spill"))
            throw new InvalidDataException("Invalid prepared escrow record.");

        var originals = new Dictionary<int, Item>();
        foreach (var original in tag.GetList<TagCompound>("originals"))
        {
            if (!original.ContainsKey("slot") || !original.ContainsKey("item") ||
                !originals.TryAdd(original.GetInt("slot"), original.Get<Item>("item")))
                throw new InvalidDataException("Invalid or duplicate prepared escrow rollback slot.");
        }

        var record = new EscrowRecord
        {
            DeadlineUtcTicks = tag.GetLong("deadline"),
            Lineage = lineage,
            Status = (CommandStatus)tag.GetInt("status"),
            OriginalSlots = originals,
            Items = new List<Item>(tag.GetList<Item>("items")),
            IsPut = tag.GetByte("put") != 0,
            Spill = tag.GetByte("spill") != 0,
            Slots = tag.GetIntArray("slots")
        };
        record.Validate();
        return record;
    }

    private void Validate()
    {
        if (DeadlineUtcTicks <= 0 || DeadlineUtcTicks > DateTime.MaxValue.Ticks || Lineage == Guid.Empty ||
            OriginalSlots is null || Items is null || Slots is null)
            throw new InvalidDataException("Invalid prepared escrow metadata.");

        foreach (var (slot, item) in OriginalSlots)
            if (slot < 0 || item is null)
                throw new InvalidDataException("Invalid prepared escrow rollback slot.");

        foreach (var item in Items)
            if (item is null)
                throw new InvalidDataException("Invalid prepared escrow item.");

        foreach (var slot in Slots)
            if (slot < 0)
                throw new InvalidDataException("Invalid prepared escrow destination slot.");
    }
}
