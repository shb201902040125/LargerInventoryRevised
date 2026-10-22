using System;
using System.Collections.Generic;
using Terraria;
using Terraria.ModLoader.IO;

namespace LargerInventory.Backend.Commands;

/// <summary>
/// 原生指令结果的 TagCompound 序列化（联机结果包用）。
/// ESCAPE 的结果由第三方自行同步，不在此序列化。
/// </summary>
public static class CommandResultSerializer
{
    private const byte KindNone = 0;
    private const byte KindItems = 1;
    private const byte KindQueryType = 2;
    private const byte KindSlotInfo = 3;
    private const byte KindInt = 4;
    private const byte KindUnsupported = 255;

    public static TagCompound Save(CommandResult result)
    {
        var tag = new TagCompound
        {
            ["version"] = 1,
            ["status"] = (int)result.Status
        };

        switch (result.Payload)
        {
            case null:
                tag["kind"] = KindNone;
                break;

            case List<Item> items:
                tag["kind"] = KindItems;
                tag["items"] = items;
                break;

            case QueryTypeResult query:
                tag["kind"] = KindQueryType;
                tag["queryType"] = query.Type;
                tag["queryCount"] = query.Count;
                tag["queryFavorites"] = query.Favorites;
                tag["queryPages"] = query.Pages;
                break;

            case SlotInfo[] slots:
                tag["kind"] = KindSlotInfo;
                tag["slotIds"] = Array.ConvertAll(slots, s => s.SlotId);
                tag["slotTypes"] = Array.ConvertAll(slots, s => s.Type);
                tag["slotStacks"] = Array.ConvertAll(slots, s => s.Stack);
                tag["slotFavorited"] = Array.ConvertAll(slots, s => s.Favorited);
                tag["slotAir"] = Array.ConvertAll(slots, s => s.IsAir);
                break;

            case int value:
                tag["kind"] = KindInt;
                tag["intValue"] = value;
                break;

            default:
                tag["kind"] = KindUnsupported;
                break;
        }

        return tag;
    }

    public static bool TryLoad(TagCompound tag, out CommandResult result)
    {
        result = default;
        if (tag is null)
        {
            return false;
        }

        var status = (CommandStatus)tag.Get<int>("status");
        var kind = tag.Get<byte>("kind");
        switch (kind)
        {
            case KindNone:
                result = new CommandResult(status);
                return true;

            case KindItems:
                result = new CommandResult(status, tag.Get<List<Item>>("items") ?? new List<Item>());
                return true;

            case KindQueryType:
                result = new CommandResult(status, new QueryTypeResult
                {
                    Type = tag.Get<int>("queryType"),
                    Count = tag.Get<int>("queryCount"),
                    Favorites = tag.Get<int>("queryFavorites"),
                    Pages = tag.Get<int[]>("queryPages") ?? Array.Empty<int>()
                });
                return true;

            case KindSlotInfo:
            {
                var ids = tag.Get<int[]>("slotIds") ?? Array.Empty<int>();
                var types = tag.Get<int[]>("slotTypes") ?? Array.Empty<int>();
                var stacks = tag.Get<int[]>("slotStacks") ?? Array.Empty<int>();
                var favorites = tag.Get<bool[]>("slotFavorited") ?? Array.Empty<bool>();
                var air = tag.Get<bool[]>("slotAir") ?? Array.Empty<bool>();
                var slots = new SlotInfo[ids.Length];
                for (var i = 0; i < ids.Length; i++)
                {
                    slots[i] = new SlotInfo
                    {
                        SlotId = ids[i],
                        Type = i < types.Length ? types[i] : 0,
                        Stack = i < stacks.Length ? stacks[i] : 0,
                        Favorited = i < favorites.Length && favorites[i],
                        IsAir = i < air.Length && air[i]
                    };
                }

                result = new CommandResult(status, slots);
                return true;
            }

            case KindInt:
                result = new CommandResult(status, tag.Get<int>("intValue"));
                return true;

            default:
                return false;
        }
    }
}
