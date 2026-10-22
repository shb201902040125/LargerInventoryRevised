using System;
using System.Collections.Generic;
using Terraria;
using Terraria.ModLoader.IO;

namespace LargerInventory.Backend.Commands;

/// <summary>
/// 原生指令请求的 TagCompound 序列化（客户端 → 服务器）。
/// ESCAPE 不序列化（第三方自行同步）。
/// </summary>
public static class CommandSerializer
{
    public static TagCompound Save(Command command)
    {
        if (command is null || command.Kind == CommandKind.Escape)
        {
            throw new NotSupportedException("Escape commands cannot be serialized.");
        }

        var tag = new TagCompound
        {
            ["version"] = 1,
            ["kind"] = (byte)command.Kind,
            ["timeout"] = command.TimeoutMs,
            ["strong"] = command.Access.IsStrong,
            ["slots"] = command.Access.Slots ?? Array.Empty<int>(),
            ["weakType"] = command.Access.Weak.Type,
            ["weakCount"] = command.Access.Weak.Count,
            ["weakAvoid"] = command.Access.Weak.AvoidFavorited
        };

        switch (command.Payload)
        {
            case TakePayload take:
                tag["takeType"] = take.Type;
                tag["takeCount"] = take.Count;
                tag["takeAvoid"] = take.AvoidFavorited;
                break;
            case PutPayload put:
                tag["putItems"] = put.Items is List<Item> list ? list : new List<Item>(put.Items ?? Array.Empty<Item>());
                tag["putSpill"] = put.Spill;
                break;
            case MovePayload move:
                tag["moveSource"] = move.SourceSlot;
                tag["moveDestination"] = move.DestinationSlot;
                break;
            case QuerySlotPayload slots:
                tag["querySlots"] = slots.Slots ?? Array.Empty<int>();
                break;
            case QueryTypePayload queryType:
                tag["queryType"] = queryType.Type;
                break;
        }

        return tag;
    }

    public static Command Load(TagCompound tag, Action<CommandResult> callback = null)
    {
        if (tag is null)
        {
            throw new ArgumentNullException(nameof(tag));
        }

        var kind = (CommandKind)tag.Get<byte>("kind");
        var timeout = tag.Get<int>("timeout");
        var strong = tag.Get<bool>("strong");
        var slots = tag.Get<int[]>("slots") ?? Array.Empty<int>();
        var access = strong
            ? AccessSet.Strong(slots)
            : AccessSet.ForWeak(new TypeSelector(
                tag.Get<int>("weakType"),
                tag.Get<int>("weakCount"),
                tag.Get<bool>("weakAvoid")));

        object payload = kind switch
        {
            CommandKind.Take => new TakePayload
            {
                Type = tag.Get<int>("takeType"),
                Count = tag.Get<int>("takeCount"),
                AvoidFavorited = tag.Get<bool>("takeAvoid")
            },
            CommandKind.Put => new PutPayload
            {
                Items = tag.Get<List<Item>>("putItems") ?? new List<Item>(),
                Spill = tag.Get<bool>("putSpill")
            },
            CommandKind.Move => new MovePayload
            {
                SourceSlot = tag.Get<int>("moveSource"),
                DestinationSlot = tag.Get<int>("moveDestination")
            },
            CommandKind.QuerySlot => new QuerySlotPayload
            {
                Slots = tag.Get<int[]>("querySlots") ?? Array.Empty<int>()
            },
            CommandKind.QueryType => new QueryTypePayload
            {
                Type = tag.Get<int>("queryType")
            },
            _ => null
        };

        return new Command(kind, timeout, access, payload, callback);
    }
}
