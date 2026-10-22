#if DEBUG
using System;
using System.Collections.Generic;
using System.Threading;
using LargerInventory.Backend.Commands;
using LargerInventory.Backend.Network;
using Terraria.ID;
using Terraria;
using Terraria.ModLoader;

namespace LargerInventory.Backend.Integration;

/// <summary>
/// 仅 Debug 构建的冒烟/调试命令。发布版不注册任何玩家命令；
/// 正式功能由上层事务层通过代码调用指令 API 实现。
/// </summary>
public sealed class InventoryDebugCommand : ModCommand
{
    public override CommandType Type => CommandType.Chat;
    public override string Command => "li";
    public override string Usage => "/li fill <type> <count> | take <type> <count> | q <type> | slot <slotId> | move <srcSlot> <dstSlot> | stats | netq <type> | netfill <type> <count>";
    public override string Description => "LargerInventory debug command (DEBUG only).";

    public override void Action(CommandCaller caller, string input, string[] args)
    {
        if (args.Length == 0)
        {
            caller.Reply(Usage, Microsoft.Xna.Framework.Color.Yellow);
            return;
        }

        var sub = args[0].ToLowerInvariant();
        if (sub is "netq" or "netfill")
        {
            HandleNetwork(args, sub, caller);
            return;
        }

        var store = Main.LocalPlayer?.GetModPlayer<InventoryPlayer>()?.Store ?? InventorySystem.Instance?.WorldStore;
        if (store is null)
        {
            caller.Reply("Store is not ready.", Microsoft.Xna.Framework.Color.Red);
            return;
        }

        switch (sub)
        {
            case "q":
            {
                if (!TryInt(args, 1, out var type))
                {
                    caller.Reply("Usage: /li q <type>", Microsoft.Xna.Framework.Color.Yellow);
                    return;
                }

                var result = Run(store, cb => CommandFactory.QueryType(type, 0, cb));
                if (result.Payload is QueryTypeResult view)
                {
                    caller.Reply($"type={view.Type} count={view.Count} favorites={view.Favorites} pages={view.Pages.Length} flags={result.Status}");
                }
                else
                {
                    caller.Reply($"query failed: {result.Status}");
                }

                return;
            }

            case "fill":
            {
                if (!TryInt(args, 1, out var type) || !TryInt(args, 2, out var count) || count <= 0)
                {
                    caller.Reply("Usage: /li fill <type> <count>", Microsoft.Xna.Framework.Color.Yellow);
                    return;
                }

                var items = new List<Item>();
                var probe = new Item();
                probe.SetDefaults(type);
                if (probe.type != type)
                {
                    caller.Reply($"invalid item type {type}", Microsoft.Xna.Framework.Color.Red);
                    return;
                }

                var left = count;
                while (left > 0)
                {
                    var stack = Math.Min(left, Math.Max(1, probe.maxStack));
                    var item = new Item();
                    item.SetDefaults(type);
                    item.stack = stack;
                    items.Add(item);
                    left -= stack;
                }

                var result = Run(store, cb => CommandFactory.Put(items, true, 0, cb));
                caller.Reply($"fill type={type} count={count} status={result.Status} remaining={CountRemaining(result)}");
                return;
            }

            case "take":
            {
                if (!TryInt(args, 1, out var type) || !TryInt(args, 2, out var count))
                {
                    caller.Reply("Usage: /li take <type> <count>", Microsoft.Xna.Framework.Color.Yellow);
                    return;
                }

                var result = Run(store, cb => CommandFactory.Take(type, count, true, 0, cb));
                var taken = result.Payload as List<Item>;
                caller.Reply($"take type={type} requested={count} status={result.Status} got={CountItems(taken)}");
                if (taken is not null)
                {
                    foreach (var item in taken)
                    {
                        caller.Player.QuickSpawnItem(null, item);
                    }
                }

                return;
            }

            case "move":
            {
                if (!TryInt(args, 1, out var source) || !TryInt(args, 2, out var destination))
                {
                    caller.Reply("Usage: /li move <srcSlot> <dstSlot>", Microsoft.Xna.Framework.Color.Yellow);
                    return;
                }

                var result = Run(store, cb => CommandFactory.Move(source, destination, 0, cb));
                caller.Reply($"move {source}->{destination} status={result.Status}");
                return;
            }

            case "slot":
            {
                if (!TryInt(args, 1, out var slotId))
                {
                    caller.Reply("Usage: /li slot <slotId>", Microsoft.Xna.Framework.Color.Yellow);
                    return;
                }

                var result = Run(store, cb => CommandFactory.QuerySlot(new[] { slotId }, cb));
                if (result.Payload is SlotInfo[] info && info.Length > 0 && !info[0].IsAir)
                {
                    caller.Reply($"slot={slotId} type={info[0].Type} stack={info[0].Stack} favorited={info[0].Favorited}");
                }
                else
                {
                    caller.Reply($"slot={slotId} air/empty status={result.Status}");
                }

                return;
            }

            case "stats":
            {
                caller.Reply($"pages={store.PageCache.AllocatedPages.Count} types={store.TypeIndex.TypeCount}");
                return;
            }

            default:
                caller.Reply(Usage, Microsoft.Xna.Framework.Color.Yellow);
                return;
        }
    }

    private static void HandleNetwork(string[] args, string sub, CommandCaller caller)
    {
        if (Main.netMode != NetmodeID.MultiplayerClient)
        {
            caller.Reply("Net commands are for multiplayer clients only.", Microsoft.Xna.Framework.Color.Yellow);
            return;
        }

        try
        {
            if (sub == "netq")
            {
                if (!TryInt(args, 1, out var type))
                {
                    caller.Reply("Usage: /li netq <type>", Microsoft.Xna.Framework.Color.Yellow);
                    return;
                }

                NetworkSystem.RequestPlayerCommand(CommandFactory.QueryType(type), result =>
                {
                    if (result.Payload is QueryTypeResult view)
                    {
                        Main.NewText($"[LI][net] type={view.Type} count={view.Count} favorites={view.Favorites} pages={view.Pages.Length} flags={result.Status}");
                    }
                    else
                    {
                        Main.NewText($"[LI][net] query failed: {result.Status}");
                    }
                });
                caller.Reply("net query sent");
                return;
            }

            if (!TryInt(args, 1, out var itemType) || !TryInt(args, 2, out var count) || count <= 0)
            {
                caller.Reply("Usage: /li netfill <type> <count>", Microsoft.Xna.Framework.Color.Yellow);
                return;
            }

            var items = BuildItems(itemType, count);
            if (items.Count == 0)
            {
                caller.Reply($"invalid item type {itemType}", Microsoft.Xna.Framework.Color.Red);
                return;
            }

            NetworkSystem.RequestPlayerCommand(CommandFactory.Put(items, true), result =>
            {
                Main.NewText($"[LI][net] fill type={itemType} count={count} status={result.Status} remaining={CountRemaining(result)}");
            });
            caller.Reply("net fill sent");
        }
        catch (Exception e)
        {
            caller.Reply($"net command failed: {e.Message}", Microsoft.Xna.Framework.Color.Red);
        }
    }

    private static List<Item> BuildItems(int type, int count)
    {
        var items = new List<Item>();
        var probe = new Item();
        probe.SetDefaults(type);
        if (probe.type != type)
        {
            return items;
        }

        var left = count;
        while (left > 0)
        {
            var stack = Math.Min(left, Math.Max(1, probe.maxStack));
            var item = new Item();
            item.SetDefaults(type);
            item.stack = stack;
            items.Add(item);
            left -= stack;
        }

        return items;
    }

    private static CommandResult Run(InventoryStore store, Func<Action<CommandResult>, Command> factory)
    {
        CommandResult result = default;
        using var done = new ManualResetEventSlim(false);
        var command = factory(r =>
        {
            result = r;
            done.Set();
        });
        store.Push(command);
        done.Wait(5000);
        return result;
    }

    private static bool TryInt(string[] args, int index, out int value)
    {
        value = 0;
        return args.Length > index && int.TryParse(args[index], out value);
    }

    private static int CountRemaining(CommandResult result)
    {
        return result.Payload is List<Item> remaining ? CountItems(remaining) : 0;
    }

    private static int CountItems(List<Item> items)
    {
        if (items is null)
        {
            return 0;
        }

        var total = 0;
        foreach (var item in items)
        {
            if (item is not null)
            {
                total += item.stack;
            }
        }

        return total;
    }
}
#endif


