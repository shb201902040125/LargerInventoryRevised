using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Terraria;

namespace LargerInventory.Backend.Commands;

/// <summary>指令结果标志。0 表示成功；多个位可组合。</summary>
[Flags]
public enum CommandStatus
{
    Ok = 0,
    Partial = 1 << 0,
    NotEnough = 1 << 1,
    NoSpace = 1 << 2,
    NotFound = 1 << 3,
    TypeMismatch = 1 << 4,
    TargetOccupied = 1 << 5,
    InvalidArg = 1 << 6,
    PageLoadFailed = 1 << 7,
    PageSaveFailed = 1 << 8,
    Saturated = 1 << 9,
    EscapeError = 1 << 10,
    InternalError = 1 << 11,

    // 以下状态由接入层/网络层/页锁产生
    Throttled = 1 << 12,
    Cancelled = 1 << 13,
    RequestTimeout = 1 << 14,
    PageBusy = 1 << 15,
    ManualInterrupt = 1 << 16,
    Preparing = 1 << 17,
    PendingDelivery = 1 << 18,
    Retired = 1 << 19,
    Skipped = 1 << 20,
    ControlFlowLimit = 1 << 21,
    RolledBack = 1 << 22

    // TIMEOUT 不属于执行器状态：接入层自行等待时由接入层定义。
}

public readonly struct CommandResult
{
    public readonly CommandStatus Status;

    /// <summary>指令返回的数据，例如 Take 取出的物品。</summary>
    public readonly object ResultPayload;

    public CommandResult(CommandStatus status, object payload = null)
    {
        Status = status;
        ResultPayload = payload;
    }

    public bool IsOk => Status == CommandStatus.Ok;

    public static CommandResult Ok(object payload = null)
    {
        return new CommandResult(CommandStatus.Ok, payload);
    }

    public static CommandResult Fail(CommandStatus status, object payload = null)
    {
        return new CommandResult(status, payload);
    }

    public bool TryGetResultPayload<T>(out T result)
    {
        if (ResultPayload is T typed)
        {
            result = typed;
            return true;
        }

        result = default;
        return false;
    }
}

public enum CommandKind
{
    NopBoundary,
    QueryType,
    QuerySlot,
    Take,
    Put,
    Move,
    Escape,
    Label,
    Br,
    TransactionAction,
    ControlTransaction
}

/// <summary>强指定：给出明确槽位；弱指定：给出类型/数量，由执行器贪心选槽。</summary>
public sealed class AccessSet
{
    public readonly bool IsStrong;
    public readonly int[] Slots;
    public readonly TypeSelector Weak;

    private AccessSet(bool strong, int[] slots, TypeSelector weak)
    {
        IsStrong = strong;
        Slots = slots;
        Weak = weak;
    }

    public static AccessSet Strong(params int[] slots)
    {
        return new AccessSet(true, slots is null ? Array.Empty<int>() : (int[])slots.Clone(), default);
    }

    public static AccessSet ForWeak(TypeSelector selector)
    {
        return new AccessSet(false, Array.Empty<int>(), selector);
    }
}

public readonly struct TypeSelector
{
    public readonly int Type;
    public readonly int Count;
    public readonly bool AvoidFavorited;

    public TypeSelector(int type, int count, bool avoidFavorited)
    {
        Type = type;
        Count = count;
        AvoidFavorited = avoidFavorited;
    }
}

public sealed class TakePayload
{
    public int Type;
    public int Count;
    public bool AvoidFavorited = true;
    public Item DestinationSnapshot;
}

public sealed class PutPayload
{
    public IList<Item> Items;
    public bool Spill;
}

public sealed class MovePayload
{
    public int SourceSlot;
    public int DestinationSlot;
}

public sealed class EscapePayload
{
    public Func<ICollection<Item>, CommandResult> Callback;
    internal bool Concurrent { get; init; }
    internal bool ReadOnly { get; init; }
    internal bool CloneItems { get; init; }
}

public sealed class QuerySlotPayload
{
    public int[] Slots;
}

public sealed class QueryTypePayload
{
    public int Type;
}

public sealed class QueryTypeResult
{
    public int Type;
    public int Count;
    public int Favorites;
    public int[] Pages;
    public int Version;
}

public sealed class SlotInfo
{
    public int SlotId;
    public int Type;
    public int Stack;
    public bool Favorited;
    public bool IsAir;
}

/// <summary>
/// 队列中的指令。入队后不可变（Payload 内的 Item 实例由调用方负责不再并发修改）。
/// Done 在指令执行完成（结果写入）后置位，供主线程热等使用。
/// </summary>
public sealed class Command
{
    private static long _nextId;
    private int _submitted;

    public readonly long Id;
    public readonly CommandKind Kind;
    private int _timeoutMs;
    public readonly long SubmittedMs;
    public readonly AccessSet Access;
    /// <summary>指令输入参数；Take 指令这里是 TakePayload，不是返回的物品列表。</summary>
    public readonly object InputPayload;
    public readonly Action<CommandResult> Callback;

    public int TimeoutMs => _timeoutMs;

    public CommandResult Result { get; private set; }

    /// <summary>仅供测试/诊断观察完成；不属于接入层契约，接入层只使用 Callback。</summary>
    internal readonly ManualResetEventSlim Done = new(false);

    public Command(CommandKind kind, int timeoutMs, AccessSet access, object payload = null,
        Action<CommandResult> callback = null)
    {
        Id = Interlocked.Increment(ref _nextId);
        Kind = kind;
        _timeoutMs = Math.Max(0, timeoutMs);
        SubmittedMs = Environment.TickCount64;
        Access = access ?? AccessSet.ForWeak(default);
        InputPayload = payload;
        Callback = callback;
    }

    /// <summary>边界指令（timeout=0）的截止时间为“立即”，用 long.MinValue 表示。</summary>
    public long Deadline => TimeoutMs == 0 ? long.MinValue : SubmittedMs + TimeoutMs;

    internal bool TryMarkSubmitted() => Interlocked.Exchange(ref _submitted, 1) == 0;

    internal void Complete(CommandResult result)
    {
        Result = result;
        Done.Set();
    }

    public static Command CreateBoundary()
    {
        return new Command(CommandKind.NopBoundary, 0, AccessSet.ForWeak(default));
    }

    /// <summary>
    /// 指令未被执行即失败时的结果。Put 会把全部物品作为未接收部分返回，调用方据此补偿，避免物品丢失。
    /// </summary>
    internal static CommandResult FailUnexecuted(Command command, CommandStatus status)
    {
        if (command.Kind == CommandKind.Put)
        {
            return CommandResult.Fail(status, ((command.InputPayload as PutPayload)?.Items ?? Array.Empty<Item>()).Distinct<Item>(ReferenceEqualityComparer.Instance).ToList());
        }

        return command.Kind == CommandKind.Take ? CommandResult.Fail(status, new List<Item>()) : CommandResult.Fail(status);
    }
}

/// <summary>指令构造入口。所有数量均为 int；完成通知只通过 callback。</summary>
public static class CommandFactory
{
    public static Command Boundary()
    {
        return Command.CreateBoundary();
    }

    public static Command QueryType(int type, int timeoutMs = 0, Action<CommandResult> callback = null)
    {
        return new Command(CommandKind.QueryType, timeoutMs, AccessSet.ForWeak(new TypeSelector(type, 0, false)),
            new QueryTypePayload { Type = type }, callback);
    }

    public static Command QuerySlot(int[] slots, Action<CommandResult> callback = null)
    {
        return new Command(CommandKind.QuerySlot, 0, AccessSet.Strong(slots),
            new QuerySlotPayload { Slots = slots is null ? null : (int[])slots.Clone() }, callback);
    }

    public static Command Take(int type, int count, bool avoidFavorited = true, int timeoutMs = 0,
        Action<CommandResult> callback = null)
    {
        return new Command(CommandKind.Take, timeoutMs,
            AccessSet.ForWeak(new TypeSelector(type, count, avoidFavorited)),
            new TakePayload { Type = type, Count = count, AvoidFavorited = avoidFavorited }, callback);
    }

    /// <summary>取出与补货目标快照兼容的物品。调用方提交后不得修改该快照。</summary>
    public static Command TakeCompatible(Item destinationSnapshot, int count, bool avoidFavorited = true,
        int timeoutMs = 0, Action<CommandResult> callback = null)
    {
        var type = destinationSnapshot?.type ?? 0;
        return new Command(CommandKind.Take, timeoutMs,
            AccessSet.ForWeak(new TypeSelector(type, count, avoidFavorited)),
            new TakePayload { Type = type, Count = count, AvoidFavorited = avoidFavorited,
                DestinationSnapshot = destinationSnapshot }, callback);
    }

    public static Command Put(IList<Item> items, bool spill = true, int timeoutMs = 0,
        Action<CommandResult> callback = null)
    {
        return new Command(CommandKind.Put, timeoutMs,
            AccessSet.ForWeak(new TypeSelector(0, 0, false)),
            new PutPayload { Items = items, Spill = spill }, callback);
    }

    public static Command Put(IList<Item> items, int[] slots, bool spill = false, int timeoutMs = 0,
        Action<CommandResult> callback = null)
    {
        return new Command(CommandKind.Put, timeoutMs,
            AccessSet.Strong(slots),
            new PutPayload { Items = items, Spill = spill }, callback);
    }

    public static Command Move(int sourceSlot, int destinationSlot, int timeoutMs = 0,
        Action<CommandResult> callback = null)
    {
        return new Command(CommandKind.Move, timeoutMs,
            AccessSet.Strong(sourceSlot, destinationSlot),
            new MovePayload { SourceSlot = sourceSlot, DestinationSlot = destinationSlot }, callback);
    }

    /// <summary>
    /// 对指定槽位运行操作，可能在工作线程执行。集合按参数顺序包含空槽，支持按索引替换元素。
    /// 默认借用仓库物品，只读回调不得修改它们。借用期间的原地修改无法回滚。
    /// cloneItems 为 true 时隔离物品变更，只读或失败时丢弃。物品引用只能在回调期间使用。
    /// 替换槽位时应移交物品所有权，每个非空槽位须持有独立的 Item 实例。
    /// 回调应自行同步共享状态，并且不能等待当前仓库中的后续命令。
    /// </summary>
    public static Command EscapeConcurrent(int[] slots, bool readOnly, Func<ICollection<Item>, CommandResult> action,
        int timeoutMs = 0, Action<CommandResult> callback = null, bool cloneItems = false)
    {
        return new Command(CommandKind.Escape, timeoutMs, AccessSet.Strong(slots),
            new EscapePayload { Callback = action, Concurrent = true, ReadOnly = readOnly, CloneItems = cloneItems }, callback);
    }

    /// <summary>默认借用非空物品。借用引用只在回调期间有效，原地修改即时生效。</summary>
    public static Command Escape(int[] slots, Func<ICollection<Item>, CommandResult> action, int timeoutMs = 0,
        Action<CommandResult> callback = null, bool cloneItems = false)
    {
        return new Command(CommandKind.Escape, timeoutMs,
            AccessSet.Strong(slots),
            new EscapePayload { Callback = action, CloneItems = cloneItems }, callback);
    }

    /// <summary>与同一次批量提交中的一条 Br 严格配对。</summary>
    public static Command Label(BranchLabel label, Action<CommandResult> callback = null) =>
        new(CommandKind.Label, 0, null, label ?? throw new ArgumentNullException(nameof(label)), callback);

    /// <summary>等待此前实际执行的指令完成。谓词为真时跳转到对应 Label。</summary>
    public static Command Br(BranchLabel target, Func<BranchContext, bool> predicate, Action<CommandResult> callback = null) =>
        new(CommandKind.Br, 0, null, new BranchPayload(target ?? throw new ArgumentNullException(nameof(target)),
            predicate ?? throw new ArgumentNullException(nameof(predicate))), callback);

    /// <summary>显式创建或恢复事务保存点。仅可通过 PushControlTransaction 提交。</summary>
    public static Command TransactionAction(Action<BranchContext> action, Action<CommandResult> callback = null) =>
        new(CommandKind.TransactionAction, 0, null, action ?? throw new ArgumentNullException(nameof(action)), callback);
}
