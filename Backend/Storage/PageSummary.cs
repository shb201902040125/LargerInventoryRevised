using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Terraria;
using Terraria.ModLoader.Default;

namespace LargerInventory.Backend.Storage;

/// <summary>页内某类型的摘要统计。Count/Favorites 均为 int，饱和在 int.MaxValue。</summary>
internal struct TypeStat
{
    public long ExactCount;
    public long ExactFavorites;
    public int Count { get => (int)Math.Min(ExactCount, int.MaxValue); set => ExactCount = value; }
    public int Favorites { get => (int)Math.Min(ExactFavorites, int.MaxValue); set => ExactFavorites = value; }

    public static int SatAdd(int a, int b)
    {
        if (b <= 0) return a;
        return a > int.MaxValue - b ? int.MaxValue : a + b;
    }

    public static int SatSub(int a, int b)
    {
        if (b <= 0) return a;
        return a < b ? 0 : a - b;
    }
}

/// <summary>
/// 页的只读摘要。页卸载后摘要仍保留在内存中，供调度与快速查询使用。
/// 摘要只在批次结束时由页内容重算，不参与指令执行的写入目标。
/// </summary>
internal sealed class PageSummary
{
    public const int SlotCount = 1024;

    public readonly ulong[] OccupiedBits = new ulong[16];
    public readonly int[] SlotTypes = new int[SlotCount];
    public readonly Dictionary<int, TypeStat> Types = new();

    /// <summary>卸载模组物品：FullName -> 数量/收藏堆数。</summary>
    public readonly Dictionary<string, TypeStat> UnloadedTypes = new();

    /// <summary>槽位 -> 卸载物品 FullName；仅卸载物品槽位存在。</summary>
    public readonly Dictionary<int, string> SlotUnloadedKeys = new();

    public int OccupiedStacks;
    public int Version;

    /// <summary>该页上次加载失败（文件缺失/损坏）。摘要为最后已知值，页索引不得被重新分配。</summary>
    public bool Unavailable;

    public int FreeSlots => SlotCount - OccupiedStacks;

    public PageSummary()
    {
    }

    public void RebuildFrom(Item[] slots)
    {
        Types.Clear();
        UnloadedTypes.Clear();
        SlotUnloadedKeys.Clear();
        Array.Clear(OccupiedBits, 0, OccupiedBits.Length);
        Array.Clear(SlotTypes, 0, SlotTypes.Length);
        OccupiedStacks = 0;

        for (var i = 0; i < SlotCount; i++)
        {
            var item = slots[i];
            if (item is null || item.IsAir)
            {
                continue;
            }

            OccupiedBits[i >> 6] |= 1UL << (i & 63);
            OccupiedStacks++;

            if (item.ModItem is UnloadedItem unloaded)
            {
                var key = unloaded.FullName;
                SlotTypes[i] = -1;
                SlotUnloadedKeys[i] = key;
                ref var unloadedStat = ref CollectionsMarshal.GetValueRefOrAddDefault(UnloadedTypes, key, out _);
                unloadedStat.ExactCount += item.stack;
                if (item.favorited)
                {
                    unloadedStat.ExactFavorites++;
                }
                continue;
            }

            SlotTypes[i] = item.type;
            ref var stat = ref CollectionsMarshal.GetValueRefOrAddDefault(Types, item.type, out _);
            stat.ExactCount += item.stack;
            if (item.favorited)
            {
                stat.ExactFavorites++;
            }
        }
    }

    public Dictionary<int, TypeStat> SnapshotTypes()
    {
        return new Dictionary<int, TypeStat>(Types);
    }

    public bool HasType(int type)
    {
        return Types.ContainsKey(type);
    }

    public void CopyFrom(PageSummary other)
    {
        Array.Copy(other.OccupiedBits, OccupiedBits, OccupiedBits.Length);
        Array.Copy(other.SlotTypes, SlotTypes, SlotTypes.Length);
        Types.Clear();
        foreach (var pair in other.Types)
        {
            Types[pair.Key] = pair.Value;
        }

        UnloadedTypes.Clear();
        foreach (var pair in other.UnloadedTypes)
        {
            UnloadedTypes[pair.Key] = pair.Value;
        }

        SlotUnloadedKeys.Clear();
        foreach (var pair in other.SlotUnloadedKeys)
        {
            SlotUnloadedKeys[pair.Key] = pair.Value;
        }

        OccupiedStacks = other.OccupiedStacks;
        Version = other.Version;
        Unavailable = other.Unavailable;
    }
}
