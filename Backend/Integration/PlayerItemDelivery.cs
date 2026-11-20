using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;
using Terraria.ModLoader.IO;

namespace LargerInventory.Backend.Integration;

internal static class PlayerItemDelivery
{
    private const string PendingKey = "pendingDelivery";
    private sealed class PendingDelivery
    {
        public List<Item> Items = new();
        public volatile TagCompound[] Snapshot = Array.Empty<TagCompound>();
        public ulong NextRetry;
    }

    private static readonly ConditionalWeakTable<Player, PendingDelivery> Pending = new();
    private static readonly ConditionalWeakTable<Item, object> ProtectedDrops = new();
    private static readonly object DropMarker = new();

    internal static bool IsProtectedDrop(Item item) => ProtectedDrops.TryGetValue(item, out _);

    public static int Give(Player player, IEnumerable<Item> items, List<Item> overflow) =>
        GiveToInventory(player is { active: true, dead: false } ? player.inventory : null, items, overflow);

    // Writes only the supplied array. The caller can plan delivery on a shallow copy.
    internal static int GiveToInventory(Item[] inventory, IEnumerable<Item> items, List<Item> overflow)
    {
        ArgumentNullException.ThrowIfNull(overflow);
        var delivered = 0;
        foreach (var input in items)
        {
            var item = input;
            if (item is null || item.IsAir)
            {
                continue;
            }

            if (inventory is not null)
            {
                var end = Math.Min(inventory.Length, Main.InventoryItemSlotsStart + Main.InventoryItemSlotsCount);
                for (var i = Main.InventoryItemSlotsStart; i < end && !item.IsAir; i++)
                {
                    var slot = inventory[i];
                    if (slot is null || slot.IsAir)
                    {
                        continue;
                    }

                    delivered += Stack(ref inventory[i], ref item);
                }

                for (var i = Main.InventoryItemSlotsStart; i < end && !item.IsAir; i++)
                {
                    if (inventory[i] is not { IsAir: true })
                    {
                        continue;
                    }

                    if (item.stack <= item.maxStack)
                    {
                        delivered += item.stack;
                        inventory[i] = item;
                        goto NextItem;
                    }

                    try
                    {
                        if (item.maxStack <= 0)
                        {
                            break;
                        }

                        var remainder = item.Clone();
                        var part = ItemLoader.TransferWithLimit(remainder, item.maxStack);
                        if (part.type != item.type || part.stack != item.maxStack ||
                            remainder.type != item.type || remainder.stack != item.stack - part.stack)
                        {
                            throw new InvalidOperationException("A split hook changed the delivery quantity or type.");
                        }

                        inventory[i] = part;
                        item = remainder;
                        delivered += part.stack;
                    }
                    catch (Exception error)
                    {
                        Log.Error("Unable to split a stack for delivery.", error);
                        break;
                    }
                }
            }

            if (!item.IsAir)
            {
                overflow.Add(item);
            }

        NextItem:;
        }

        return delivered;
    }

    private static int Stack(ref Item destination, ref Item source)
    {
        if (destination.type != source.type || destination.maxStack <= destination.stack)
        {
            return 0;
        }

        try
        {
            var nextDestination = destination.Clone();
            var nextSource = source.Clone();
            if (!ItemLoader.TryStackItems(nextDestination, nextSource, out var moved))
            {
                return 0;
            }

            if (moved <= 0 || moved > source.stack || nextSource.stack != source.stack - moved ||
                (long)nextDestination.stack != (long)destination.stack + moved ||
                nextDestination.stack > nextDestination.maxStack || nextDestination.type != destination.type ||
                (nextSource.stack > 0 && nextSource.type != source.type))
            {
                throw new InvalidOperationException("A stacking hook changed the delivery quantity or type.");
            }

            destination = nextDestination;
            source = nextSource;
            return moved;
        }
        catch (Exception error)
        {
            Log.Error("A stacking hook failed during delivery.", error);
            return 0;
        }
    }

    internal static void Refill(Player player, int slotId, List<Item> taken)
    {
        var slot = player.inventory[slotId];
        for (var index = 0; index < taken.Count;)
        {
            var item = taken[index];
            if (slot.IsAir && item.stack <= item.maxStack)
            {
                player.inventory[slotId] = slot = item;
                taken.RemoveAt(index);
                continue;
            }

            if (!slot.IsAir)
            {
                Stack(ref slot, ref item);
                player.inventory[slotId] = slot;
                taken[index] = item;
            }

            if (item.IsAir)
            {
                taken.RemoveAt(index);
            }
            else
            {
                index++;
            }
        }
    }

    internal static bool TryDrop(Player player, Item item) => player is not null && player.active && !player.dead && TryDropAt(player.Center, item);

    private static bool TryDropAt(Vector2 position, Item item)
    {
        if (Main.netMode == NetmodeID.MultiplayerClient || item is null || item.IsAir)
        {
            return false;
        }

        // Vanilla reuses an occupied slot as a last resort. Pending delivery is preferable
        // to destroying an unrelated world item when the world is at capacity.
        var slots = Math.Min(Main.maxItems, Main.item.Length);
        var hasFreeSlot = false;
        for (var i = 0; i < slots; i++)
        {
            if (!Main.item[i].active && Main.timeItemSlotCannotBeReusedFor[i] == 0)
            {
                hasFreeSlot = true;
                break;
            }
        }

        if (!hasFreeSlot)
        {
            return false;
        }

        try
        {
            var index = Item.NewItem(new Terraria.DataStructures.EntitySource_Misc("LargerInventoryDelivery"), position, item);
            if ((uint)index >= (uint)slots || Main.item[index] is not { active: true } spawned || spawned.IsAir)
            {
                return false;
            }

            ProtectedDrops.GetValue(spawned, _ => DropMarker);
            return true;
        }
        catch (Exception error)
        {
            Log.Error("Unable to spawn an overflow item.", error);
            return false;
        }
    }

    internal static int DropOverflow(Player player, List<Item> overflow)
    {
        var dropped = 0;
        overflow.RemoveAll(item =>
        {
            if (!TryDrop(player, item))
            {
                return false;
            }

            dropped += item.stack;
            return true;
        });
        return dropped;
    }

    internal static void CompensatePickup(Player player, Vector2 position, List<Item> leftovers)
    {
        leftovers.RemoveAll(item => TryDropAt(position, item));
        Hold(player, leftovers);
    }

    internal static void Hold(Player player, IEnumerable<Item> items)
    {
        if (player is null)
        {
            return;
        }

        var state = Pending.GetOrCreateValue(player);
        foreach (var item in items)
        {
            if (item is not null && !item.IsAir)
            {
                state.Items.Add(item);
            }
        }

        Snapshot(state);
    }

    private static void Snapshot(PendingDelivery state)
    {
        var tags = new TagCompound[state.Items.Count];
        for (var index = 0; index < tags.Length; index++)
        {
            tags[index] = ItemIO.Save(state.Items[index]);
        }

        state.Snapshot = tags;
    }

    internal static void LoadPending(Player player, TagCompound tag)
    {
        var state = Pending.GetOrCreateValue(player);
        foreach (var itemTag in tag.GetList<TagCompound>(PendingKey))
        {
            var item = ItemIO.Load(itemTag);
            if (!item.IsAir)
            {
                state.Items.Add(item);
            }
        }

        state.Snapshot = new List<TagCompound>(tag.GetList<TagCompound>(PendingKey)).ToArray();
    }

    internal static void SavePending(Player player, TagCompound tag)
    {
        if (Pending.TryGetValue(player, out var state) && state.Snapshot is { Length: > 0 } snapshot)
        {
            tag[PendingKey] = snapshot;
        }
    }

    internal static void RetrySinglePlayer(Player player)
    {
        if (player is null || !player.active || player.dead || !Pending.TryGetValue(player, out var state) ||
            state.Items.Count == 0 || Main.GameUpdateCount < state.NextRetry)
        {
            return;
        }

        state.NextRetry = Main.GameUpdateCount + 60;
        var overflow = new List<Item>();
        Give(player, state.Items, overflow);
        DropOverflow(player, overflow);
        state.Items = overflow;
        Snapshot(state);
    }
}
