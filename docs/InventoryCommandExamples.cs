using System;
using System.Collections.Generic;
using LargerInventory.Backend;
using LargerInventory.Backend.Commands;
using Terraria;

namespace LargerInventory.Examples;

// 在游戏线程调用。store 是当前世界中已经绑定的单机或服务端仓库，由 InventorySystem 推进。
// 联机客户端应通过联机请求访问服务端仓库。这里演示直接访问权威仓库的 C# 接口。
public static class InventoryCommandExamples
{
    // 提交前从来源扣留整批物品，提交后停止修改列表和物品实例。
    // 被拒绝或放不下时，结果携带未接收的物品，按回调归还给原来源。
    public static void Put(InventoryStore store, IList<Item> reservedItems, Action<List<Item>> returnItems)
        => store.Push(CreatePut(reservedItems, returnItems, 0));

    // 部分成功也会返回已从仓库扣除的物品。调用方负责接收和处理交付余量。
    public static void Take(InventoryStore store, int itemType, int count, Action<List<Item>> receiveItems)
        => store.Push(CreateTake(itemType, count, receiveItems, 0));

    // 类型查询返回数量、收藏数量及所在页号。调用方需要判断 Saturated 等状态。
    public static void QueryType(InventoryStore store, int itemType, Action<CommandResult> completed)
        => store.Push(CommandFactory.QueryType(itemType, callback: completed));

    // 查询多个槽位，结果通过 TryGetResultPayload<SlotInfo[]> 读取。
    public static void QuerySlots(InventoryStore store, int[] slots, Action<CommandResult> completed)
        => store.Push(CommandFactory.QuerySlot(slots, completed));

    // 每次使用新指令。整组连续入队且统一检查队列容量。每条指令独立结算，可能跨帧完成。
    public static void PushBatch(
        InventoryStore store, int itemType, IList<Item> reservedItems,
        Action<List<Item>> returnItems, Action<List<Item>> receiveItems,
        Action<CommandResult> queried)
    {
        const int gatherMs = 10;
        store.PushBatch(new[]
        {
            CreatePut(reservedItems, returnItems, gatherMs),
            CommandFactory.QueryType(itemType, timeoutMs: gatherMs, callback: queried),
            CommandFactory.Boundary(), // 立即触发此前队列前缀的调度，仍受执行预算限制。
            CreateTake(itemType, 20, receiveItems, gatherMs),
            CommandFactory.Boundary() // 为后段提供独立的调度边界。
        });
    }

    // 追加到当前 FIFO 队尾。若要在一组指令中间插入边界，在构造数组时放入对应位置。
    // 边界表示调度分隔，业务需要持久化确认时应使用持久化事务的完成结果。
    public static void AppendBoundary(InventoryStore store)
        => store.Push(CommandFactory.Boundary());

    private static Command CreatePut(IList<Item> items, Action<List<Item>> returnItems, int gatherMs)
        => CommandFactory.Put(items, timeoutMs: gatherMs, callback: result =>
        {
            if (result.TryGetResultPayload<List<Item>>(out var remaining) && remaining.Count > 0)
                returnItems(remaining);
        });

    private static Command CreateTake(int type, int count, Action<List<Item>> receiveItems, int gatherMs)
        => CommandFactory.Take(type, count, avoidFavorited: true, timeoutMs: gatherMs, callback: result =>
        {
            if (result.TryGetResultPayload<List<Item>>(out var taken) && taken.Count > 0)
                receiveItems(taken);
        });
}
