using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using Terraria.ModLoader.IO;

namespace LargerInventory.Backend.Storage;

internal enum StoreMetaStatus
{
    /// <summary>不存在 store.meta（新存储或尚未保存过）。</summary>
    Missing,

    /// <summary>已成功读取（来自正式文件或备份）。</summary>
    Valid,

    /// <summary>正式文件无效，已从可能较旧的备份恢复；调用方必须从页文件重建摘要。</summary>
    RecoveredFromBackup,

    /// <summary>存在但无法读取，调用方需要从页文件重建索引。</summary>
    Invalid
}

/// <summary>
/// store.meta：存储级别的页目录与每页快速视图（摘要）。加载时只读取 meta，不反序列化 Item。
/// 正式文件损坏时回退到 store.meta.bak。
/// </summary>
internal static class StoreMeta
{
    private const string FileName = "store.meta";
    private const int MetaVersion = 2;
    private const int OccupiedBytes = 16 * 8;

    public static void Save(string storeDirectory, IReadOnlyList<ItemPage> pages)
    {
        Directory.CreateDirectory(storeDirectory);
        var journal = new StoreRedoJournal(storeDirectory);
        journal.Recover();
        var snapshot = BuildSnapshot(pages);
        journal.CommitPrepared(() =>
        {
            var writes = new List<StoreRedoJournal.Write>();
            Stage(storeDirectory, snapshot, writes);
            return writes;
        });
    }

    // The snapshot contains detached primitive tags. Reading the prior copy and compressing this
    // snapshot need no ItemIO or mod hooks and can run in the journal's I/O worker.
    internal static void Stage(string storeDirectory, TagCompound snapshot, List<StoreRedoJournal.Write> writes)
    {
        var previous = StoreRedoJournal.ReadOptional(Path.Combine(storeDirectory, FileName));
        if (previous is not null)
        {
            using var input = new MemoryStream(previous, writable: false);
            if (TryReadStream(input, out _, out _, out var unsupported))
            {
                var backup = StoreRedoJournal.ReadOptional(Path.Combine(storeDirectory, FileName + ".bak"));
                var unsupportedBackup = false;
                if (backup is not null)
                {
                    using var backupInput = new MemoryStream(backup, writable: false);
                    TryReadStream(backupInput, out _, out _, out unsupportedBackup);
                }
                if (!unsupportedBackup)
                    writes.Add(new(FileName + ".bak", previous));
            }
            else if (unsupported)
                throw new InvalidDataException("Refusing to replace an unsupported store.meta version.");
        }
        using var stream = new MemoryStream();
        TagIO.ToStream(snapshot, stream, compress: true);
        writes.Add(new(FileName, stream.ToArray()));
    }

    public static StoreMetaStatus Load(string storeDirectory, out Dictionary<int, PageSummary> summaries, out string error)
    {
        summaries = new Dictionary<int, PageSummary>();
        error = null;

        var path = Path.Combine(storeDirectory, FileName);
        var backup = path + ".bak";
        if (!File.Exists(path) && !File.Exists(backup))
        {
            return StoreMetaStatus.Missing;
        }

        if (TryReadFile(path, out var primary, out var primaryError))
        {
            summaries = primary;
            return StoreMetaStatus.Valid;
        }

        if (File.Exists(backup) && TryReadFile(backup, out var recovered, out _))
        {
            Log.Warn($"store.meta unreadable ({primaryError}); recovered from backup.");
            summaries = recovered;
            return StoreMetaStatus.RecoveredFromBackup;
        }

        error = $"primary: {primaryError ?? "missing"}";
        return StoreMetaStatus.Invalid;
    }

    private static bool TryReadFile(string path, out Dictionary<int, PageSummary> summaries, out string error)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return TryReadStream(stream, out summaries, out error, out _);
        }
        catch (Exception e)
        {
            summaries = new Dictionary<int, PageSummary>();
            error = e is FileNotFoundException or DirectoryNotFoundException ? "missing" : e.Message;
            return false;
        }
    }

    private static bool TryReadStream(Stream stream, out Dictionary<int, PageSummary> summaries, out string error, out bool unsupported)
    {
        summaries = new Dictionary<int, PageSummary>();
        error = null;
        unsupported = false;
        try
        {
            var root = TagIO.FromStream(stream, compressed: true);
            if (!root.ContainsKey("version"))
                throw new InvalidDataException("Missing store.meta version.");
            var version = root.Get<int>("version");
            if (version != MetaVersion)
            {
                unsupported = true;
                error = $"Unsupported store.meta version {version}.";
                return false;
            }

            var pages = root.Get<List<TagCompound>>("pages") ?? new List<TagCompound>();
            foreach (var pageTag in pages)
            {
                var pageIndex = pageTag.Get<int>("pageIndex");
                if (pageIndex < 0 || pageIndex >= PageDirectory.MaxPageCount)
                {
                    error = $"page index {pageIndex} out of range";
                    return false;
                }

                if (summaries.ContainsKey(pageIndex))
                {
                    error = $"duplicate page index {pageIndex}";
                    return false;
                }

                summaries[pageIndex] = ReadSummary(pageTag);
            }

            return true;
        }
        catch (Exception e)
        {
            error = e.Message;
            return false;
        }
    }

    internal static TagCompound BuildSnapshot(IReadOnlyList<ItemPage> pages)
    {
        return new TagCompound
        {
            ["version"] = MetaVersion,
            ["pages"] = BuildPageList(pages)
        };
    }

    internal static List<TagCompound> BuildPageList(IReadOnlyList<ItemPage> pages)
    {
        var list = new List<TagCompound>(pages.Count);
        foreach (var page in pages)
        {
            // 加载失败的页也要写入元数据：保留页索引和最后已知摘要，避免下次启动把该索引
            // 当作空闲页重新分配，同时给用户留下恢复原页文件的机会。
            var tag = new TagCompound
            {
                ["pageIndex"] = page.PageIndex,
                ["version"] = page.Summary.Version,
                ["failed"] = (byte)(page.LoadFailed ? 1 : 0),
                ["occupiedBits"] = BitsToBytes(page.Summary.OccupiedBits),
                ["slotTypes"] = (int[])page.Summary.SlotTypes.Clone()
            };

            var types = new List<TagCompound>(page.Summary.Types.Count);
            foreach (var pair in page.Summary.Types)
            {
                types.Add(new TagCompound
                {
                    ["type"] = pair.Key,
                    ["count"] = pair.Value.ExactCount,
                    ["favorites"] = pair.Value.ExactFavorites
                });
            }

            tag["types"] = types;

            var unloaded = new List<TagCompound>(page.Summary.UnloadedTypes.Count);
            foreach (var pair in page.Summary.UnloadedTypes)
            {
                unloaded.Add(new TagCompound
                {
                    ["key"] = pair.Key,
                    ["count"] = pair.Value.ExactCount,
                    ["favorites"] = pair.Value.ExactFavorites
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
            Version = pageTag.Get<int>("version"),
            Unavailable = pageTag.Get<byte>("failed") != 0
        };

        var occupied = pageTag.Get<byte[]>("occupiedBits") ?? Array.Empty<byte>();
        if (occupied.Length != OccupiedBytes)
        {
            throw new InvalidDataException("occupiedBits has invalid length");
        }

        BytesToBits(occupied, summary.OccupiedBits);

        var slotTypes = pageTag.Get<int[]>("slotTypes") ?? Array.Empty<int>();
        if (slotTypes.Length != PageSummary.SlotCount)
        {
            throw new InvalidDataException("slotTypes has invalid length");
        }

        Array.Copy(slotTypes, summary.SlotTypes, slotTypes.Length);

        var types = pageTag.Get<List<TagCompound>>("types") ?? new List<TagCompound>();
        foreach (var typeTag in types)
        {
            summary.Types[typeTag.Get<int>("type")] = new TypeStat
            {
                ExactCount = typeTag.Get<long>("count"),
                ExactFavorites = typeTag.Get<long>("favorites")
            };
        }

        var unloaded = pageTag.Get<List<TagCompound>>("unloaded") ?? new List<TagCompound>();
        foreach (var entry in unloaded)
        {
            summary.UnloadedTypes[entry.Get<string>("key")] = new TypeStat
            {
                ExactCount = entry.Get<long>("count"),
                ExactFavorites = entry.Get<long>("favorites")
            };
        }

        var unloadedSlots = pageTag.Get<List<TagCompound>>("unloadedSlots") ?? new List<TagCompound>();
        foreach (var entry in unloadedSlots)
        {
            var slot = entry.Get<int>("slot");
            if (slot < 0 || slot >= PageSummary.SlotCount)
            {
                throw new InvalidDataException($"unloaded slot {slot} out of range");
            }

            summary.SlotUnloadedKeys[slot] = entry.Get<string>("key");
        }

        var occupiedStacks = 0;
        for (var i = 0; i < 16; i++)
        {
            occupiedStacks += BitOperations.PopCount(summary.OccupiedBits[i]);
        }

        summary.OccupiedStacks = occupiedStacks;
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
        for (var i = 0; i < bits.Length; i++)
        {
            bits[i] = BitConverter.ToUInt64(bytes, i * 8);
        }
    }
}
