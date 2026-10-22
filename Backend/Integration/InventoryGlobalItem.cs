using System;
using System.Collections.Generic;
using LargerInventory.Backend;
using LargerInventory.Backend.Commands;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;

namespace LargerInventory.Backend.Integration;

internal static class IntegrationPolicy
{
#if DEBUG
    public const bool AutoHooksEnabled = true;
#else
    // 发布版不向玩家提供"等效扩展背包"行为；由上层事务层通过指令 API 自行实现。
    public const bool AutoHooksEnabled = false;
#endif
}

/// <summary>
/// 接入层钩子：异步指令 + 回调后处理。没有热等、没有结果超时。
/// 指令完成只通过 Callback；游戏对象操作统一投递到主线程队列。
/// </summary>
public sealed class InventoryGlobalItem : GlobalItem
{
    public override bool OnPickup(WorldItem item, Player player)
    {
        if (!IntegrationPolicy.AutoHooksEnabled || Main.netMode != NetmodeID.SinglePlayer)
        {
            return true;
        }

        var store = player?.GetModPlayer<InventoryPlayer>()?.Store;
        if (store is null)
        {
            return true;
        }

        var inner = item.inner;
        if (inner is null || inner.IsAir || inner.stack <= 0)
        {
            return true;
        }

        // 玩家背包还能收下时，不抢原版行为。
        try
        {
            if (ItemLoader.ItemSpace(inner, player))
            {
                return true;
            }
        }
        catch (Exception e)
        {
            Log.Error("ItemSpace hook failed; fall back to vanilla pickup.", e);
            return true;
        }

        var copy = inner.Clone();
        var position = item.position;

        try
        {
            item.TurnToAirAndSync();
        }
        catch (Exception e)
        {
            Log.Error("Failed to remove world item; fall back to vanilla pickup.", e);
            return true;
        }

        try
        {
            store.Push(CommandFactory.Put(new List<Item> { copy }, true, 0,
                result => OnPickupPutCompleted(result, position, player)));
        }
        catch (Exception e)
        {
            Log.Error("Failed to push pickup Put command; respawning item.", e);
            InventorySystem.EnqueueMainThreadAction(() => RespawnItem(player, position, copy));
        }

        // 不等待结果：物品已先从世界移除，成功即入库，失败由回调补发。
        return false;
    }

    public override bool ConsumeItem(Item item, Player player)
    {
        if (!IntegrationPolicy.AutoHooksEnabled || Main.netMode != NetmodeID.SinglePlayer)
        {
            return true;
        }

        var store = player?.GetModPlayer<InventoryPlayer>()?.Store;
        if (store is null || item is null || item.IsAir)
        {
            return true;
        }

        var missing = item.maxStack - item.stack;
        if (missing <= 0)
        {
            return true;
        }

        try
        {
            store.Push(CommandFactory.Take(item.type, missing, true, 0,
                result => OnConsumeTakeCompleted(result, player)));
        }
        catch (Exception e)
        {
            Log.Error("Failed to push consume Take command.", e);
        }

        // 原版消耗照常进行；补货异步完成。
        return true;
    }

    private static void OnPickupPutCompleted(CommandResult result, Vector2 position, Player player)
    {
        if (result.Payload is List<Item> remaining && remaining.Count > 0)
        {
            InventorySystem.EnqueueMainThreadAction(() =>
            {
                foreach (var item in remaining)
                {
                    if (item is not null && !item.IsAir && item.stack > 0)
                    {
                        RespawnItem(player, position, item);
                    }
                }
            });
            return;
        }

        if (!result.IsOk)
        {
            // 没有 remaining payload 的失败（例如 INTERNAL_ERROR）无法还原具体物品；
            // 接入层只记录，避免盲目复制。
            Log.Error($"Pickup Put failed without remaining payload: {result.Status}");
        }
    }

    private static void OnConsumeTakeCompleted(CommandResult result, Player player)
    {
        if (result.Payload is not List<Item> taken || taken.Count == 0)
        {
            return;
        }

        InventorySystem.EnqueueMainThreadAction(() =>
        {
            if (player is null || !player.active)
            {
                return;
            }

            foreach (var item in taken)
            {
                if (item is not null && !item.IsAir && item.stack > 0)
                {
                    player.QuickSpawnItem(null, item);
                }
            }
        });
    }

    private static void RespawnItem(Player player, Vector2 position, Item item)
    {
        if (item is null || item.IsAir || item.stack <= 0)
        {
            return;
        }

        if (player is not null && player.active)
        {
            player.QuickSpawnItem(null, item);
            return;
        }

        Item.NewItem(null, position, item);
    }
}

