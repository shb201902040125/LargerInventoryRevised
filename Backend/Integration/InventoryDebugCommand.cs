using System;
using System.Collections.Generic;
using LargerInventory.Backend.Commands;
using LargerInventory.Backend.Crafting;
using LargerInventory.Backend.Network;
using LargerInventory.Backend.Storage;
using Terraria.ID;
using Terraria;
using Terraria.ModLoader;

namespace LargerInventory.Backend.Integration;

/// <summary>
/// 测试命令。默认在 Mod Config 中关闭；只有服务器/单机配置开启时才允许使用。
/// 修复指令具有破坏性，联机时只能由服务器修复请求者自己的玩家 Store。
/// </summary>
public sealed class InventoryDebugCommand : ModCommand
{
    public override CommandType Type => CommandType.Chat;
    public override string Command => "li";
    public override string Usage => "/li fill <type> <count> | take <type> <count> | q <type> | slot <slotId> | move <srcSlot> <dstSlot> | recipes [-world] | stats | repair [world|status|cancel] | pause [on|off] | netq <type> [world] | netfill <type> <count> | nettake <type> <count> [manual] | netmove <srcSlot> <dstSlot> | netresult <operation-id> | netcollect <operation-id> [operation-id ...]";
    public override string Description => "LargerInventory test command (requires config).";

    public override void Action(CommandCaller caller, string input, string[] args)
    {
        var config = ModContent.GetInstance<LargerInventoryConfig>();
        if (config is null || !config.EnableTestCommands)
        {
            caller.Reply("LargerInventory test commands are disabled. Enable 'Enable test commands' in Mod Configs.", Microsoft.Xna.Framework.Color.Yellow);
            return;
        }

        if (args.Length == 0)
        {
            caller.Reply(Usage, Microsoft.Xna.Framework.Color.Yellow);
            return;
        }

        var sub = args[0].ToLowerInvariant();
        Log.Info($"/li invoked: {string.Join(" ", args)}");
        if (sub == "recipes")
        {
            bool world = args.Length == 2 && args[1].Equals("-world", StringComparison.OrdinalIgnoreCase);
            if (args.Length != 1 && !world)
            {
                caller.Reply("Usage: /li recipes [-world]");
                return;
            }
            CraftingQueries.QueryAvailableRecipes(result =>
            {
                if (result.ResultPayload is not CraftingAvailability availability)
                {
                    caller.Reply($"Recipe query failed: {result.Status}");
                    return;
                }
                int available = 0;
                for (int index = 0; index < availability.RecipeCount; index++)
                    if (availability.IsAvailable(index)) available++;
                caller.Reply($"{(world ? "World" : "Personal")} warehouse: {available}/{availability.RecipeCount} candidate recipes.");
            }, world);
            return;
        }
        if (sub is "netq" or "netfill" or "nettake" or "netmove" or "netresult" or "netcollect")
        {
            HandleNetwork(args, sub, caller);
            return;
        }

        if (sub == "repair")
        {
            HandleRepair(args, caller);
            return;
        }

        if (sub == "pause")
        {
            HandlePause(args, caller);
            return;
        }

        if (Main.netMode != NetmodeID.SinglePlayer)
        {
            caller.Reply("Use the /li net commands in multiplayer.");
            return;
        }

        var store = caller.Player?.GetModPlayer<InventoryPlayer>()?.Store;
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
                if (result.ResultPayload is QueryTypeResult view)
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

                var items = BuildItems(type, count);
                if (items.Count == 0)
                {
                    caller.Reply("Invalid item type or a request exceeding 4096 stacks.");
                    return;
                }

                store.Push(CommandFactory.Put(items, true, 0, result =>
                    caller.Reply($"fill type={type} count={count} status={result.Status} remaining={CountRemaining(result)}")));
                return;
            }

            case "take":
            {
                if (!TryInt(args, 1, out var type) || !TryInt(args, 2, out var count))
                {
                    caller.Reply("Usage: /li take <type> <count>", Microsoft.Xna.Framework.Color.Yellow);
                    return;
                }

                var player = caller.Player;
                store.Push(CommandFactory.Take(type, count, true, 0, result =>
                {
                    if (!result.TryGetResultPayload<List<Item>>(out var taken))
                    {
                        caller.Reply($"take failed: {result.Status}");
                        return;
                    }

                    var got = CountItems(taken);
                    var overflow = new List<Item>();
                    var delivered = PlayerItemDelivery.Give(player, taken, overflow);
                    var dropped = PlayerItemDelivery.DropOverflow(player, overflow);
                    var pending = CountItems(overflow);
                    PlayerItemDelivery.Hold(player, overflow);
                    caller.Reply($"take type={type} requested={count} status={result.Status} got={got} delivered={delivered} dropped={dropped} pending={pending}");
                }));

                return;
            }

            case "move":
            {
                if (!TryInt(args, 1, out var source) || !TryInt(args, 2, out var destination))
                {
                    caller.Reply("Usage: /li move <srcSlot> <dstSlot>", Microsoft.Xna.Framework.Color.Yellow);
                    return;
                }

                store.Push(CommandFactory.Move(source, destination, 0,
                    result => caller.Reply($"move {source}->{destination} status={result.Status}")));
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
                if (result.ResultPayload is SlotInfo[] info && info.Length > 0 && info[0] is { IsAir: false })
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

    private static void HandlePause(string[] args, CommandCaller caller)
    {
        if (Main.netMode != NetmodeID.SinglePlayer)
        {
            caller.Reply("Pause is available in singleplayer only.");
            return;
        }

        var pause = InventorySystem.CommandsPaused;
        if (args.Length > 1)
        {
            if (string.Equals(args[1], "on", StringComparison.OrdinalIgnoreCase))
            {
                pause = true;
            }
            else if (string.Equals(args[1], "off", StringComparison.OrdinalIgnoreCase))
            {
                pause = false;
            }
            else
            {
                caller.Reply("Usage: /li pause [on|off]", Microsoft.Xna.Framework.Color.Yellow);
                return;
            }
        }
        else
        {
            pause = !pause;
        }

        if (pause)
        {
            InventorySystem.PauseAllCommands();
            caller.Reply("commands paused: new commands rejected; queued commands interrupted with ManualInterrupt.", Microsoft.Xna.Framework.Color.Yellow);
        }
        else
        {
            InventorySystem.ResumeAllCommands();
            caller.Reply("commands resumed.", Microsoft.Xna.Framework.Color.Green);
        }
    }

    private static void HandleRepair(string[] args, CommandCaller caller)
    {
        var world = false;
        var status = false;
        var cancel = false;
        if (args.Length > 1)
        {
            if (string.Equals(args[1], "world", StringComparison.OrdinalIgnoreCase))
            {
                world = true;
            }
            else if (string.Equals(args[1], "status", StringComparison.OrdinalIgnoreCase))
            {
                status = true;
            }
            else if (string.Equals(args[1], "cancel", StringComparison.OrdinalIgnoreCase))
            {
                cancel = true;
            }
            else
            {
                caller.Reply("Usage: /li repair [world|status|cancel]", Microsoft.Xna.Framework.Color.Yellow);
                return;
            }
        }

        if (Main.netMode != NetmodeID.SinglePlayer)
        {
            caller.Reply("修复仅可在单机运行。", Microsoft.Xna.Framework.Color.Yellow);
            return;
        }

        var store = world
            ? InventorySystem.Instance?.WorldStore
            : Main.LocalPlayer?.GetModPlayer<InventoryPlayer>()?.Store;

        if (store is null)
        {
            caller.Reply(world ? "World store is not ready." : "Player store is not ready.", Microsoft.Xna.Framework.Color.Red);
            return;
        }

        if (status)
        {
            var snapshot = store.GetRepairSnapshot();
            caller.Reply(snapshot is null
                ? "repair: no task has run yet."
                : $"repair: total={snapshot.TotalPages} scanned={snapshot.ScannedPages} repaired={snapshot.RepairedPages} lockFailed={snapshot.LockFailedPages} failed={snapshot.FailedPages} canceled={snapshot.Canceled} running={store.IsRepairRunning}");
            return;
        }

        if (cancel)
        {
            store.CancelPageRepair();
            caller.Reply("repair cancel requested; it will stop after the current page.", Microsoft.Xna.Framework.Color.Yellow);
            return;
        }

        if (!store.StartPageRepair(
                _ => { },
                report => Main.NewText($"[LI] repair {(world ? "world" : "player")}: total={report.TotalPages} repaired={report.RepairedPages} recovered={report.RecoverablePages} healthy={report.HealthyPages} lockFailed={report.LockFailedPages} failed={report.FailedPages} ioErrors={report.IoErrorPages} unsupported={report.UnsupportedVersionPages} modErrors={report.ModErrorPages} pages=[{string.Join(",", report.RepairedPageIndices)}] archive={report.ArchiveDirectory ?? "-"}"),
                out var error))
        {
            caller.Reply($"repair {(world ? "world" : "player")} failed: {error}", Microsoft.Xna.Framework.Color.Red);
            return;
        }

        caller.Reply($"repair {(world ? "world" : "player")} started in background.", Microsoft.Xna.Framework.Color.Yellow);
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

                void ShowQuery(CommandResult result)
                {
                    if (result.ResultPayload is QueryTypeResult view)
                    {
                        Main.NewText($"[LI][net] type={view.Type} count={view.Count} favorites={view.Favorites} pages={view.Pages.Length} flags={result.Status}");
                    }
                    else
                    {
                        Main.NewText($"[LI][net] query failed: {result.Status}");
                    }
                }

                var command = CommandFactory.QueryType(type);
                if (args.Length > 2 && string.Equals(args[2], "world", StringComparison.OrdinalIgnoreCase))
                {
                    NetworkSystem.RequestWorldCommand(command, ShowQuery);
                }
                else
                {
                    NetworkSystem.RequestPlayerCommand(command, ShowQuery);
                }

                caller.Reply("net query sent");
                return;
            }

            if (sub == "netcollect")
            {
                if (args.Length < 2 || args.Length > NetworkSystem.MaxEscrowConfirmationBatch + 1)
                {
                    caller.Reply("Usage: /li netcollect <operation-id> [operation-id ...]");
                    return;
                }
                var ids = new List<Guid>(args.Length - 1);
                for (int index = 1; index < args.Length; index++)
                {
                    if (!Guid.TryParse(args[index], out var id) || id == Guid.Empty || ids.Contains(id))
                    {
                        caller.Reply("Provide distinct operation IDs.");
                        return;
                    }
                    ids.Add(id);
                }
                NetworkSystem.ConsumeWorldEscrows(ids, (id, result) =>
                    Main.NewText($"[LI][net] {id:N} {result.Status} (remaining={result.ResultPayload})"));
                return;
            }

            if (sub == "netresult")
            {
                if (args.Length != 2 || !Guid.TryParse(args[1], out var id) || id == Guid.Empty)
                {
                    caller.Reply($"Usage: /li {sub} <operation-id>");
                    return;
                }

                NetworkSystem.RequestWorldResult(id, ReplyNetworkResult);
                return;
            }

            if (sub == "netmove")
            {
                if (!TryInt(args, 1, out var source) || !TryInt(args, 2, out var destination))
                {
                    caller.Reply("Usage: /li netmove <source> <destination>");
                    return;
                }

                var id = NetworkSystem.RequestRecoverableWorldTransaction(CommandFactory.Move(source, destination), ReplyNetworkResult);
                caller.Reply($"world request {id:N}");
                return;
            }

            if (!TryInt(args, 1, out var itemType) || !TryInt(args, 2, out var count) || count <= 0)
            {
                caller.Reply($"Usage: /li {sub} <type> <count>", Microsoft.Xna.Framework.Color.Yellow);
                return;
            }

            var request = sub switch
            {
                "netfill" => NetworkSystem.RequestRecoverableDebugFill(itemType, count, ReplyNetworkResult),
                "nettake" when args.Length > 3 && args[3].Equals("manual", StringComparison.OrdinalIgnoreCase) =>
                    NetworkSystem.PrepareWorldTake(CommandFactory.Take(itemType, count, true), ReplyNetworkResult),
                "nettake" => NetworkSystem.RequestRecoverableWorldTransaction(CommandFactory.Take(itemType, count, true), ReplyNetworkResult),
                _ => Guid.Empty
            };
            caller.Reply($"world request {request:N}");
        }
        catch (Exception e)
        {
            caller.Reply($"net command failed: {e.Message}", Microsoft.Xna.Framework.Color.Red);
        }
    }

    private static void ReplyNetworkResult(CommandResult result)
    {
        var detail = result.ResultPayload is int count ? $" count={count}" : string.Empty;
        Main.NewText($"[LI][net] {result.Status}{detail}");
    }

    private static List<Item> BuildItems(int type, int count)
    {
        var items = new List<Item>();
        if (type <= 0 || type >= ItemLoader.ItemCount || count <= 0)
        {
            return items;
        }

        var probe = new Item();
        probe.SetDefaults(type);
        if (probe.type != type || (long)count > 4096L * Math.Max(1, probe.maxStack))
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
        var command = factory(_ => { });
        store.Push(command);
        // Pump 有每次批次数上限；只 Pump 一次可能让后排队的 Take 尚未执行，
        // 但它之后仍会修改 Store，造成结果未交付。最多排空队列上限对应的批次数。
        var maxPasses = store.Config.MaxQueuedCommands;
        for (var pass = 0; pass < maxPasses && !command.Done.IsSet; pass++)
        {
            if (store.Pump() == 0)
            {
                break;
            }
        }

        return command.Done.IsSet ? command.Result : CommandResult.Fail(CommandStatus.InternalError);
    }

    private static bool TryInt(string[] args, int index, out int value)
    {
        value = 0;
        return args.Length > index && int.TryParse(args[index], out value);
    }

    private static long CountRemaining(CommandResult result)
    {
        return result.ResultPayload is List<Item> remaining ? CountItems(remaining) : 0;
    }

    private static long CountItems(List<Item> items)
    {
        if (items is null)
        {
            return 0;
        }

        long total = 0;
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

