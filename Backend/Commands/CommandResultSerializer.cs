using System;
using LargerInventory.Backend.Storage;
using Terraria.ModLoader.IO;

namespace LargerInventory.Backend.Commands;

/// <summary>
/// 原生指令结果的 TagCompound 序列化（联机结果包用）。
/// ESCAPE 的结果由第三方自行同步，不在此序列化。
/// </summary>
public static class CommandResultSerializer
{
    private const byte KindNone = 0;
    private const byte KindQueryType = 2;
    private const byte KindSlotInfo = 3;
    private const byte KindInt = 4;
    private const byte KindRepair = 5;
    private const byte KindUnsupported = 255;

    public static TagCompound Save(CommandResult result)
    {
        var tag = new TagCompound
        {
            ["version"] = 1,
            ["status"] = (int)result.Status
        };

        switch (result.ResultPayload)
        {
            case null:
                tag["kind"] = KindNone;
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
                tag["slotIds"] = Array.ConvertAll(slots, s => s?.SlotId ?? -1);
                tag["slotTypes"] = Array.ConvertAll(slots, s => s?.Type ?? 0);
                tag["slotStacks"] = Array.ConvertAll(slots, s => s?.Stack ?? 0);
                // NBT 不支持 bool[]：用 byte[] 0/1 表示。
                tag["slotFavorited"] = Array.ConvertAll(slots, s => (byte)((s?.Favorited ?? false) ? 1 : 0));
                tag["slotAir"] = Array.ConvertAll(slots, s => (byte)((s?.IsAir ?? true) ? 1 : 0));
                break;

            case int value:
                tag["kind"] = KindInt;
                tag["intValue"] = value;
                break;

            case RepairReport report:
                tag["kind"] = KindRepair;
                tag["repairTotal"] = report.TotalPages;
                tag["repairScanned"] = report.ScannedPages;
                tag["repairHealthy"] = report.HealthyPages;
                tag["repairRecovered"] = report.RecoverablePages;
                tag["repairCount"] = report.RepairedPages;
                tag["repairInUse"] = report.SkippedInUsePages;
                tag["repairLockFailed"] = report.LockFailedPages;
                tag["repairFailed"] = report.FailedPages;
                tag["repairRemaining"] = report.RemainingFailedPages;
                tag["repairCanceled"] = (byte)(report.Canceled ? 1 : 0);
                tag["repairMetaFailed"] = (byte)(report.MetaSaveFailed ? 1 : 0);
                tag["repairPages"] = report.RepairedPageIndices ?? Array.Empty<int>();
                tag["repairLockPages"] = report.LockFailedPageIndices ?? Array.Empty<int>();
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
        if (tag is null || tag.Get<int>("version") != 1 || !tag.ContainsKey("status") || !tag.ContainsKey("kind"))
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
                var favorites = tag.Get<byte[]>("slotFavorited") ?? Array.Empty<byte>();
                var air = tag.Get<byte[]>("slotAir") ?? Array.Empty<byte>();
                var slots = new SlotInfo[ids.Length];
                for (var i = 0; i < ids.Length; i++)
                {
                    slots[i] = new SlotInfo
                    {
                        SlotId = ids[i],
                        Type = i < types.Length ? types[i] : 0,
                        Stack = i < stacks.Length ? stacks[i] : 0,
                        Favorited = i < favorites.Length && favorites[i] != 0,
                        IsAir = i < air.Length && air[i] != 0
                    };
                }

                result = new CommandResult(status, slots);
                return true;
            }

            case KindInt:
                result = new CommandResult(status, tag.Get<int>("intValue"));
                return true;

            case KindRepair:
                result = new CommandResult(status, new RepairReport
                {
                    TotalPages = tag.Get<int>("repairTotal"),
                    ScannedPages = tag.Get<int>("repairScanned"),
                    HealthyPages = tag.Get<int>("repairHealthy"),
                    RecoverablePages = tag.Get<int>("repairRecovered"),
                    RepairedPages = tag.Get<int>("repairCount"),
                    SkippedInUsePages = tag.Get<int>("repairInUse"),
                    LockFailedPages = tag.Get<int>("repairLockFailed"),
                    FailedPages = tag.Get<int>("repairFailed"),
                    RemainingFailedPages = tag.Get<int>("repairRemaining"),
                    Canceled = tag.Get<byte>("repairCanceled") != 0,
                    MetaSaveFailed = tag.Get<byte>("repairMetaFailed") != 0,
                    RepairedPageIndices = tag.Get<int[]>("repairPages") ?? Array.Empty<int>(),
                    LockFailedPageIndices = tag.Get<int[]>("repairLockPages") ?? Array.Empty<int>()
                });
                return true;

            default:
                return false;
        }
    }
}
