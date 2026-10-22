using System;
using System.IO;
using System.Text;
using Terraria;
using Terraria.ModLoader.IO;

namespace LargerInventory.Backend.Storage;

internal sealed class PageLoadResult
{
    public bool NotFound;
    public bool Success;
    public string Error;
    public Item[] Items;
    public PageSummary QuickView;

    public static PageLoadResult Missing() => new() { NotFound = true };
    public static PageLoadResult Ok(Item[] items, PageSummary quickView) => new() { Success = true, Items = items, QuickView = quickView };
    public static PageLoadResult Fail(string error) => new() { Error = error };
}

internal sealed class PageSaveResult
{
    public bool Success;
    public string Error;

    public static PageSaveResult Ok() => new() { Success = true };
    public static PageSaveResult Fail(string error) => new() { Error = error };
}

/// <summary>页文件读写接口。每页一个文件；当前实现为同步 IO（执行线程内调用）。</summary>
internal interface IPageFileStore
{
    bool Available { get; }
    PageLoadResult Load(int pageIndex);
    PageSaveResult Save(int pageIndex, Item[] items, PageSummary summary);
}

/// <summary>持久化空实现：不读写文件；页不会被真正卸载时使用。</summary>
internal sealed class NullPageFileStore : IPageFileStore
{
    public bool Available => false;

    public PageLoadResult Load(int pageIndex) => PageLoadResult.Missing();

    public PageSaveResult Save(int pageIndex, Item[] items, PageSummary summary) => PageSaveResult.Ok();
}

/// <summary>每页一个文件的持久化实现（format v1，见 v0.5 方案第 8 章）。</summary>
internal sealed class FilePageFileStore : IPageFileStore
{
    private const uint Magic = 0x4750494C; // "LIPG"
    private const ushort FormatVersion = 1;
    private const string Extension = ".lipg";

    private readonly string _pagesDirectory;

    public FilePageFileStore(string storeDirectory)
    {
        _pagesDirectory = Path.Combine(storeDirectory, "pages");
        Directory.CreateDirectory(_pagesDirectory);
    }

    public bool Available => true;

    public PageLoadResult Load(int pageIndex)
    {
        var path = GetPath(pageIndex);
        if (!File.Exists(path))
        {
            return PageLoadResult.Missing();
        }

        try
        {
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length < 24)
            {
                return PageLoadResult.Fail("page file too short");
            }

            using var stream = new MemoryStream(bytes);
            using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
            if (reader.ReadUInt32() != Magic)
            {
                return PageLoadResult.Fail("bad magic");
            }

            var version = reader.ReadUInt16();
            if (version != FormatVersion)
            {
                return PageLoadResult.Fail($"unsupported page format {version}");
            }

            _ = reader.ReadUInt16(); // flags
            var storedPageIndex = reader.ReadInt32();
            if (storedPageIndex != pageIndex)
            {
                return PageLoadResult.Fail("page index mismatch");
            }

            var expectedCrc = reader.ReadUInt32();
            var quickViewLength = reader.ReadInt32();
            var itemsLength = reader.ReadInt32();
            var payloadLength = quickViewLength + itemsLength;
            if (payloadLength < 0 || payloadLength > bytes.Length - 24)
            {
                return PageLoadResult.Fail("bad payload length");
            }

            var payload = new byte[payloadLength];
            Buffer.BlockCopy(bytes, 24, payload, 0, payloadLength);
            if (Crc32.Compute(payload, 0, payload.Length) != expectedCrc)
            {
                return PageLoadResult.Fail("crc mismatch");
            }

            using var payloadStream = new MemoryStream(payload);
            using var payloadReader = new BinaryReader(payloadStream, Encoding.UTF8, leaveOpen: true);
            var quickView = ReadQuickView(payloadReader);
            var items = ReadItems(payloadReader);
            return PageLoadResult.Ok(items, quickView);
        }
        catch (Exception e)
        {
            return PageLoadResult.Fail(e.Message);
        }
    }

    public PageSaveResult Save(int pageIndex, Item[] items, PageSummary summary)
    {
        try
        {
            using var quickStream = new MemoryStream();
            using (var quickWriter = new BinaryWriter(quickStream, Encoding.UTF8, leaveOpen: true))
            {
                WriteQuickView(quickWriter, summary);
            }

            using var itemStream = new MemoryStream();
            using (var itemWriter = new BinaryWriter(itemStream, Encoding.UTF8, leaveOpen: true))
            {
                WriteItems(itemWriter, items);
            }

            var quickBytes = quickStream.ToArray();
            var itemBytes = itemStream.ToArray();
            var payload = new byte[quickBytes.Length + itemBytes.Length];
            Buffer.BlockCopy(quickBytes, 0, payload, 0, quickBytes.Length);
            Buffer.BlockCopy(itemBytes, 0, payload, quickBytes.Length, itemBytes.Length);
            var crc = Crc32.Compute(payload, 0, payload.Length);

            var path = GetPath(pageIndex);
            var tempPath = path + ".tmp";
            var backupPath = path + ".bak";

            using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var writer = new BinaryWriter(fs, Encoding.UTF8, leaveOpen: false))
            {
                writer.Write(Magic);
                writer.Write(FormatVersion);
                writer.Write((ushort)0);
                writer.Write(pageIndex);
                writer.Write(crc);
                writer.Write(quickBytes.Length);
                writer.Write(itemBytes.Length);
                writer.Write(payload);
                writer.Flush();
                fs.Flush(true);
            }

            if (File.Exists(path))
            {
                if (File.Exists(backupPath))
                {
                    File.Delete(backupPath);
                }

                File.Replace(tempPath, path, backupPath, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(tempPath, path);
            }

            return PageSaveResult.Ok();
        }
        catch (Exception e)
        {
            return PageSaveResult.Fail(e.Message);
        }
    }

    private string GetPath(int pageIndex)
    {
        return Path.Combine(_pagesDirectory, pageIndex.ToString("D7") + Extension);
    }

    private static void WriteQuickView(BinaryWriter writer, PageSummary summary)
    {
        for (var i = 0; i < summary.OccupiedBits.Length; i++)
        {
            writer.Write(summary.OccupiedBits[i]);
        }

        for (var i = 0; i < summary.SlotTypes.Length; i++)
        {
            writer.Write(summary.SlotTypes[i]);
        }

        writer.Write((ushort)summary.Types.Count);
        foreach (var pair in summary.Types)
        {
            writer.Write(pair.Key);
            writer.Write(pair.Value.Count);
            writer.Write(pair.Value.Favorites);
        }

        writer.Write((ushort)summary.UnloadedTypes.Count);
        foreach (var pair in summary.UnloadedTypes)
        {
            writer.Write(pair.Key);
            writer.Write(pair.Value.Count);
            writer.Write(pair.Value.Favorites);
        }
    }

    private static PageSummary ReadQuickView(BinaryReader reader)
    {
        var summary = new PageSummary();
        for (var i = 0; i < summary.OccupiedBits.Length; i++)
        {
            summary.OccupiedBits[i] = reader.ReadUInt64();
        }

        for (var i = 0; i < summary.SlotTypes.Length; i++)
        {
            summary.SlotTypes[i] = reader.ReadInt32();
        }

        for (var i = 0; i < summary.OccupiedBits.Length; i++)
        {
            summary.OccupiedStacks += System.Numerics.BitOperations.PopCount(summary.OccupiedBits[i]);
        }

        var typeCount = reader.ReadUInt16();
        for (var i = 0; i < typeCount; i++)
        {
            var type = reader.ReadInt32();
            summary.Types[type] = new TypeStat
            {
                Count = reader.ReadInt32(),
                Favorites = reader.ReadInt32()
            };
        }

        var unloadedCount = reader.ReadUInt16();
        for (var i = 0; i < unloadedCount; i++)
        {
            var key = reader.ReadString();
            summary.UnloadedTypes[key] = new TypeStat
            {
                Count = reader.ReadInt32(),
                Favorites = reader.ReadInt32()
            };
        }

        return summary;
    }

    private static void WriteItems(BinaryWriter writer, Item[] items)
    {
        var occupied = 0;
        for (var i = 0; i < items.Length; i++)
        {
            if (items[i] is not null && !items[i].IsAir)
            {
                occupied++;
            }
        }

        writer.Write((ushort)occupied);
        for (var i = 0; i < items.Length; i++)
        {
            var item = items[i];
            if (item is null || item.IsAir)
            {
                continue;
            }

            writer.Write((ushort)i);
            var tag = ItemIO.Save(item);
            using var ms = new MemoryStream();
            TagIO.ToStream(tag, ms, compress: false);
            var bytes = ms.ToArray();
            writer.Write(bytes.Length);
            writer.Write(bytes);
        }
    }

    private static Item[] ReadItems(BinaryReader reader)
    {
        var items = new Item[ItemPage.SlotCount];
        for (var i = 0; i < items.Length; i++)
        {
            items[i] = new Item();
        }

        var count = reader.ReadUInt16();
        for (var i = 0; i < count; i++)
        {
            var slot = reader.ReadUInt16();
            var length = reader.ReadInt32();
            if (length < 0 || length > 64 * 1024 * 1024)
            {
                throw new InvalidDataException("bad item tag length");
            }

            var bytes = reader.ReadBytes(length);
            using var ms = new MemoryStream(bytes);
            var tag = TagIO.FromStream(ms, false);
            var item = ItemIO.Load(tag);
            if (item is not null)
            {
                items[slot] = item;
            }
        }

        return items;
    }
}

/// <summary>CRC32（IEEE）实现，避免额外依赖。</summary>
internal static class Crc32
{
    private static readonly uint[] Table = CreateTable();

    private static uint[] CreateTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            var value = i;
            for (var j = 0; j < 8; j++)
            {
                value = (value & 1) != 0 ? 0xEDB88320u ^ (value >> 1) : value >> 1;
            }

            table[i] = value;
        }

        return table;
    }

    public static uint Compute(byte[] data, int offset, int length)
    {
        var crc = 0xFFFFFFFFu;
        for (var i = offset; i < offset + length; i++)
        {
            crc = Table[(crc ^ data[i]) & 0xFF] ^ (crc >> 8);
        }

        return ~crc;
    }
}

internal sealed class PageLoadException : Exception
{
    public PageLoadException(string message) : base(message) { }
}





