using System;
using System.Collections.Generic;
using LargerInventory.Backend.Commands;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;

namespace LargerInventory.Backend.Integration;

public sealed class InventoryGlobalItem : GlobalItem
{
    private static readonly HashSet<Item> QueuedRefills = new(ReferenceEqualityComparer.Instance);

    public override void Load() => On_Player.PickupItem += PickupOverflow;

    public override void Unload()
    {
        On_Player.PickupItem -= PickupOverflow;
        Clear();
    }

    internal static void Clear() => QueuedRefills.Clear();

    private static InventoryStore GetPickupStore(Item item, Player player)
    {
        if (Main.netMode != NetmodeID.SinglePlayer || player is null || !player.active || player.dead ||
            item is null || item.IsAir || InventorySystem.CommandsPaused ||
            PlayerItemDelivery.IsProtectedDrop(item) ||
            ModContent.GetInstance<LargerInventoryConfig>()?.AutoPickup != true)
        {
            return null;
        }

        return player.GetModPlayer<InventoryPlayer>()?.Store;
    }

    // Let the vanilla pickup path run even when there is no space. The detour below sees
    // only the remainder returned by vanilla, after all pickup hooks have accepted it.
    public override bool ItemSpace(Item item, Player player) => GetPickupStore(item, player) is not null;

    private static Item PickupOverflow(On_Player.orig_PickupItem orig, Player player, int playerIndex, int worldSlot, Item item)
    {
        var eligible = GetPickupStore(item, player);
        var remainder = orig(player, playerIndex, worldSlot, item);
        if (eligible is null || GetPickupStore(remainder, player) != eligible ||
            worldSlot < 0 || worldSlot >= Main.item.Length || !ReferenceEquals(Main.item[worldSlot], remainder))
        {
            return remainder;
        }

        var empty = new Item();
        var position = remainder.Center;
        var command = CommandFactory.Put(new List<Item> { remainder }, true, 0, result =>
        {
            if (result.ResultPayload is List<Item> leftovers && leftovers.Count > 0)
            {
                PlayerItemDelivery.CompensatePickup(player, position, leftovers);
            }
        });
        Main.item[worldSlot] = empty;
        eligible.Push(command);
        return empty;
    }

    public override void OnConsumeItem(Item item, Player player)
    {
        if (Main.netMode != NetmodeID.SinglePlayer || player is null || player.dead ||
            item is null || item.IsAir || item.maxStack <= 1 ||
            ModContent.GetInstance<LargerInventoryConfig>()?.AutoRefill != true ||
            !Array.Exists(player.inventory, slot => ReferenceEquals(slot, item)) ||
            !QueuedRefills.Add(item))
        {
            return;
        }

        Item snapshot;
        try
        {
            snapshot = item.Clone();
        }
        catch (Exception error)
        {
            QueuedRefills.Remove(item);
            Log.Error("Unable to snapshot the consumed item for refill.", error);
            return;
        }

        var slotId = Array.IndexOf(player.inventory, item);
        InventorySystem.EnqueueMainThreadAction(() => QueueRefill(player, slotId, item, snapshot));
    }

    private static void QueueRefill(Player player, int slotId, Item original, Item snapshot)
    {
        var store = player.GetModPlayer<InventoryPlayer>()?.Store;
        if (store is null || !TryGetRefillTarget(player, slotId, original, snapshot, out _, out var gap))
        {
            QueuedRefills.Remove(original);
            return;
        }

        store.Push(CommandFactory.TakeCompatible(snapshot, gap, true, 0, result =>
        {
            QueuedRefills.Remove(original);
            if (result.ResultPayload is not List<Item> taken || taken.Count == 0)
            {
                return;
            }

            if (TryGetRefillTarget(player, slotId, original, snapshot, out _, out _))
            {
                PlayerItemDelivery.Refill(player, slotId, taken);
            }

            // The target may have changed while the request was queued or a stacking hook ran.
            // Return the exact remainder to the warehouse or retain it for the original player.
            if (taken.Count > 0)
            {
                store.Push(CommandFactory.Put(taken, true, 0, put =>
                {
                    if (put.ResultPayload is List<Item> remaining)
                    {
                        PlayerItemDelivery.Hold(player, remaining);
                    }
                }));
            }
        }));
    }

    private static bool TryGetRefillTarget(Player player, int slotId, Item original, Item snapshot, out Item target, out int gap)
    {
        target = null;
        gap = 0;
        if (!player.active || player.dead || (uint)slotId >= (uint)player.inventory.Length)
        {
            return false;
        }

        var slot = player.inventory[slotId];
        if (slot is null || !ReferenceEquals(slot, original) && !slot.IsAir)
        {
            return false;
        }

        if (!slot.IsAir && (slot.type != snapshot.type || slot.prefix != snapshot.prefix))
        {
            return false;
        }

        target = slot;
        gap = slot.IsAir ? snapshot.maxStack : slot.maxStack - slot.stack;
        return gap > 0;
    }
}
