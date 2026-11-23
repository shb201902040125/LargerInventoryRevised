using System;
using System.Collections.Generic;
using LargerInventory.Backend;
using LargerInventory.Backend.Commands;
using Terraria;

namespace LargerInventory.Examples;

// 在权威仓库所在的游戏线程提交，由 InventorySystem 推进。每次调用都会构造新指令。
public static class InventoryTransactionExamples
{
    // 两笔存入都成功才提交。调用方先扣留两组物品，提交后停止修改列表和 Item。
    // 两组输入须持有互不重复的 Item 实例。回滚或拒绝时，各自的回调归还原物品。
    public static void DepositBoth(InventoryStore store,
        IList<Item> firstItems, int[] firstSlots, IList<Item> secondItems, int[] secondSlots,
        Action<List<Item>> returnItems, Action<CommandResult> completed)
    {
        var first = CommandFactory.Put(firstItems, firstSlots, callback: result => PassItems(result, returnItems));
        var second = CommandFactory.Put(secondItems, secondSlots, callback: result => PassItems(result, returnItems));
        var finish = new BranchLabel();
        var skipSecondIfFirstFails = CommandFactory.Br(finish, context => !Succeeded(context, first));

        store.PushControlTransaction(new[] { first, skipSecondIfFirstFails, second, CommandFactory.Label(finish) },
            settle: context => Decide(context, Succeeded(context, first) && Succeeded(context, second)),
            callback: completed);
    }

    // 必须取足指定数量。库存不足时，即使 Take 已部分预留，也会回滚整笔事务。
    // receiveItems 只会收到最终提交的物品，可以在这里转交给玩家。
    public static void WithdrawExactly(InventoryStore store, int itemType, int count,
        Action<List<Item>> receiveItems, Action<CommandResult> completed)
    {
        var take = CommandFactory.Take(itemType, count, callback: result => PassItems(result, receiveItems));
        store.PushControlTransaction(new[] { take }, settle: context =>
            Decide(context, context.TryGetResult(take, out var result) && result.IsOk &&
                result.TryGetResultPayload<TakePreview>(out var preview) && preview.Count == count), callback: completed);
    }

    // 先试首选槽。即使首选只移动了一部分，也先恢复源槽和目标槽，再尝试备用槽。
    // 三个槽位应各不相同。任一方案完整成功后就提交，两者都失败则恢复事务入口。
    public static void MoveWithFallback(InventoryStore store, int source, int preferred, int fallback,
        Action<CommandResult> completed)
    {
        ControlSavepoint savepoint = null;
        var finish = new BranchLabel();
        var save = CommandFactory.TransactionAction(context => savepoint = context.CreateSavepoint());
        var tryPreferred = CommandFactory.Move(source, preferred);
        var acceptPreferred = CommandFactory.Br(finish, context => Succeeded(context, tryPreferred));
        var undoPreferred = CommandFactory.TransactionAction(context => context.Restore(savepoint));
        var tryFallback = CommandFactory.Move(source, fallback);

        store.PushControlTransaction(new[]
        {
            save, tryPreferred, acceptPreferred, undoPreferred, tryFallback, CommandFactory.Label(finish)
        }, settle: context => Decide(context, Succeeded(context, tryPreferred) || Succeeded(context, tryFallback)),
            callback: completed);
    }

    // 后跳重复预留，每轮只能使用尚未预留的库存。任何一轮未取足就整笔回滚。
    // 全部成功后，Take 的最终回调一次收到各轮兑现的物品。
    public static void WithdrawInRounds(InventoryStore store, int itemType, int perRound, int rounds,
        Action<List<Item>> receiveItems, Action<CommandResult> completed)
    {
        if (perRound <= 0) throw new ArgumentOutOfRangeException(nameof(perRound));
        if (rounds <= 0) throw new ArgumentOutOfRangeException(nameof(rounds));

        var completedRounds = 0;
        var repeat = new BranchLabel();
        var take = CommandFactory.Take(itemType, perRound, callback: result => PassItems(result, receiveItems));
        var loop = CommandFactory.Br(repeat, context =>
        {
            if (!context.TryGetResult(take, out var result) || !result.IsOk ||
                !result.TryGetResultPayload<TakePreview>(out var preview) || preview.Count != perRound)
                return false;

            completedRounds++;
            return completedRounds < rounds;
        });
        store.PushControlTransaction(new[] { CommandFactory.Label(repeat), take, loop },
            settle: context => Decide(context, completedRounds == rounds), callback: completed);
    }

    private static bool Succeeded(BranchContext context, Command command) =>
        context.TryGetResult(command, out var result) && result.IsOk;

    private static ControlTransactionDecision Decide(BranchContext context, bool operationSucceeded) =>
        context.ExecutionStatus == CommandStatus.Ok && operationSucceeded
            ? ControlTransactionDecision.Commit
            : ControlTransactionDecision.Rollback;

    private static void PassItems(CommandResult result, Action<List<Item>> receive)
    {
        if (result.TryGetResultPayload<List<Item>>(out var items) && items.Count > 0)
            receive(items);
    }
}
