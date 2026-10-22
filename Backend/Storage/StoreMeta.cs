using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using Terraria.ModLoader.IO;

namespace LargerInventory.Backend.Storage;

/// <summary>
/// store.meta：作用域/页目录/每页快速视图索引。加载时只读 meta（不加载 Item 页），
/// 供调度/筛选恢复快速视图；页数据按需加载。
/// </summary>
internal static class StoreMeta
{
    private const string FileName = "store.meta";
    private const int MetaVersion = 1;

    public static void Save(string storeDirectory, IReadOnlyList<ItemPage> pages)
    {
        var root = new TagCompound
        {
            ["version"] = MetaVersion,
            ["pages"] = BuildPageList(pages)
        };

        Directory.CreateDirectory(storeDirectory);
        var path = Path.Combine(storeDirectory, FileName);
        var temp = path + ".tmp";
        TagIO.ToFile(root, temp, compress: true);
        if (File.Exists(path))
        {
            var backup = path + ".bak";
            if (File.Exists(backup))
            {
                File.Delete(backup);
            }

            File.Replace(temp, path, backup, ignoreMetadataErrors: true);
        }
        else
        {
            File.Move(temp, path);
        }
    }

    public static Dictionary<int, PageSummary> Load(string storeDirectory)
    {
        var result = new Dictionary<int, PageSummary>();
        var path = Path.Combine(storeDirectory, FileName);
        if (!File.Exists(path))
        {
            return result;
        }

        try
        {
            var root = TagIO.FromFile(path, compressed: true);
            if (root.Get<int>("version") != MetaVersion)
            {
                Log.Warn($"store.meta version mismatch at {path}; ignoring.");
                return result;
            }

            var pages = root.Get<List<TagCompound>>("pages") ?? new List<TagCompound>();
            foreach (var pageTag in pages)
            {
                var pageIndex = pageTag.Get<int>("pageIndex");
                result[pageIndex] = ReadSummary(pageTag);
            }
        }
        catch (Exception e)
        {
            Log.Error($"Failed to read store.meta at {path}.", e);
        }

        return result;
    }

    private static List<TagCompound> BuildPageList(IReadOnlyList<ItemPage> pages)
    {
        var list = new List<TagCompound>(pages.Count);
        foreach (var page in pages)
        {
            var tag = new TagCompound
            {
                ["pageIndex"] = page.PageIndex,
                ["version"] = page.Summary.Version,
                ["occupiedBits"] = BitsToBytes(page.Summary.OccupiedBits),
                ["slotTypes"] = page.Summary.SlotTypes
            };

            var types = new List<TagCompound>(page.Summary.Types.Count);
            foreach (var pair in page.Summary.Types)
            {
                types.Add(new TagCompound
                {
                    ["type"] = pair.Key,
                    ["count"] = pair.Value.Count,
                    ["favorites"] = pair.Value.Favorites
                });
            }

            tag["types"] = types;

            var unloaded = new List<TagCompound>(page.Summary.UnloadedTypes.Count);
            foreach (var pair in page.Summary.UnloadedTypes)
            {
                unloaded.Add(new TagCompound
                {
                    ["key"] = pair.Key,
                    ["count"] = pair.Value.Count,
                    ["favorites"] = pair.Value.Favorites
                });
            }

            tag["unloaded"] = unloaded;

            var unloadedSlots = new List<TagCompound>(page.Summary.SlotUnloadedKeys.Count);
            foreach (var pair in page.Summary.SlotUnloadedKeys)
            {
                unloadedSlots.Add(new TagCompound
                {
                    ["slot"] = pair.Key,
                    ["key"] = pair.Value
                });
            }

            tag["unloadedSlots"] = unloadedSlots;
            list.Add(tag);
        }

        return list;
    }

    private static PageSummary ReadSummary(TagCompound pageTag)
    {
        var summary = new PageSummary
        {
            Version = pageTag.Get<int>("version")
        };

        BytesToBits(pageTag.Get<byte[]>("occupiedBits") ?? Array.Empty<byte>(), summary.OccupiedBits);

        var slotTypes = pageTag.Get<int[]>("slotTypes") ?? Array.Empty<int>();
        Array.Copy(slotTypes, summary.SlotTypes, Math.Min(slotTypes.Length, summary.SlotTypes.Length));

        var types = pageTag.Get<List<TagCompound>>("types") ?? new List<TagCompound>();
        foreach (var typeTag in types)
        {
            summary.Types[typeTag.Get<int>("type")] = new TypeStat
            {
                Count = typeTag.Get<int>("count"),
                Favorites = typeTag.Get<int>("favorites")
            };
        }

        var unloaded = pageTag.Get<List<TagCompound>>("unloaded") ?? new List<TagCompound>();
        foreach (var entry in unloaded)
        {
            summary.UnloadedTypes[entry.Get<string>("key")] = new TypeStat
            {
                Count = entry.Get<int>("count"),
                Favorites = entry.Get<int>("favorites")
            };
        }

        var unloadedSlots = pageTag.Get<List<TagCompound>>("unloadedSlots") ?? new List<TagCompound>();
        foreach (var entry in unloadedSlots)
        {
            summary.SlotUnloadedKeys[entry.Get<int>("slot")] = entry.Get<string>("key");
        }

        var occupied = 0;
        for (var i = 0; i < 16; i++)
        {
            occupied += BitOperations.PopCount(summary.OccupiedBits[i]);
        }

        summary.OccupiedStacks = occupied;
        return summary;
    }

    private static byte[] BitsToBytes(ulong[] bits)
    {
        var bytes = new byte[bits.Length * 8];
        for (var i = 0; i < bits.Length; i++)
        {
            BitConverter.GetBytes(bits[i]).CopyTo(bytes, i * 8);
        }

        return bytes;
    }

    private static void BytesToBits(byte[] bytes, ulong[] bits)
    {
        var length = Math.Min(bytes.Length / 8, bits.Length);
        for (var i = 0; i < length; i++)
        {
            bits[i] = BitConverter.ToUInt64(bytes, i * 8);
        }
    }
}


