using System;
using System.Collections.Generic;
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
    InternalError = 1 << 11

    // TIMEOUT 不属于执行器状态：接入层自行等待时由接入层定义。
}

public readonly struct CommandResult
{
    public readonly CommandStatus Status;
    public readonly object Payload;

    public CommandResult(CommandStatus status, object payload = null)
    {
        Status = status;
        Payload = payload;
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
}

public enum CommandKind
{
    NopBoundary,
    QueryType,
    QuerySlot,
    Take,
    Put,
    Move,
    Escape
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
        return new AccessSet(true, slots ?? Array.Empty<int>(), default);
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

    public readonly long Id;
    public readonly CommandKind Kind;
    private int _timeoutMs;
    public readonly long SubmittedMs;
    public readonly AccessSet Access;
    public readonly object Payload;
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
        Payload = payload;
        Callback = callback;
    }

    /// <summary>边界指令（timeout=0）的截止时间为“立即”，用 long.MinValue 表示。</summary>
    public long Deadline => TimeoutMs == 0 ? long.MinValue : SubmittedMs + TimeoutMs;

    internal void Complete(CommandResult result)
    {
        Result = result;
        Done.Set();
    }

    public static Command CreateBoundary()
    {
        return new Command(CommandKind.NopBoundary, 0, AccessSet.ForWeak(default));
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
            new QuerySlotPayload { Slots = slots }, callback);
    }

    public static Command Take(int type, int count, bool avoidFavorited = true, int timeoutMs = 0,
        Action<CommandResult> callback = null)
    {
        return new Command(CommandKind.Take, timeoutMs,
            AccessSet.ForWeak(new TypeSelector(type, count, avoidFavorited)),
            new TakePayload { Type = type, Count = count, AvoidFavorited = avoidFavorited }, callback);
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

    public static Command Escape(int[] slots, Func<ICollection<Item>, CommandResult> action, int timeoutMs = 0,
        Action<CommandResult> callback = null)
    {
        return new Command(CommandKind.Escape, timeoutMs,
            AccessSet.Strong(slots),
            new EscapePayload { Callback = action }, callback);
    }
}


