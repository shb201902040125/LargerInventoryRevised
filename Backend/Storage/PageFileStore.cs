using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Terraria;
using Terraria.ModLoader.IO;

namespace LargerInventory.Backend.Storage;

internal enum PageFileState { Missing, Valid, Corrupt, IoError, UnsupportedVersion, ModError, RecoverableFromBackup }

internal readonly record struct PageFileInspection(PageFileState State);

internal sealed class PageLoadResult
{
    public PageFileState State;
    public string Error;
    public Item[] Items;
    public byte[] Bytes;
    public bool FromBackup;
    public bool NotFound => State == PageFileState.Missing;
    public bool Success => State == PageFileState.Valid;
    public static PageLoadResult Missing() => new() { State = PageFileState.Missing };
}

internal sealed class PageSaveResult
{
    public bool Success;
    public string Error;
}

internal interface IPageFileStore
{
    bool Available { get; }
    PageLoadResult Load(int pageIndex);
    IReadOnlyList<int> ListPages();
}

internal sealed class NullPageFileStore : IPageFileStore
{
    public bool Available => false;
    public PageLoadResult Load(int pageIndex) => PageLoadResult.Missing();
    public IReadOnlyList<int> ListPages() => Array.Empty<int>();
}

/// <summary>V2 stores exact quick-view totals and a per-write generation token.</summary>
internal sealed class FilePageFileStore : IPageFileStore
{
    private const uint Magic = 0x4750494C;
    private const ushort CurrentVersion = 2;
    private const int PayloadOffset = 24;
    private const int HeaderSize = 40;
    private const string Extension = ".lipg";
    private const int MaxItemTagBytes = 64 * 1024 * 1024;
    private readonly string _root;

    public FilePageFileStore(string storeDirectory)
    {
        _root = storeDirectory;
        Directory.CreateDirectory(Path.Combine(_root, "pages"));
    }

    public bool Available => true;

    internal static string FileName(int index) => "pages/" + index.ToString("D7", CultureInfo.InvariantCulture) + Extension;

    public PageLoadResult Load(int pageIndex)
    {
        var primary = ReadCopy(pageIndex, backup: false, materialize: true);
        if (primary.Success || !CanReplace(primary.State))
            return primary;
        var backup = ReadCopy(pageIndex, backup: true, materialize: true);
        if (backup.Success)
        {
            Log.Warn($"Page {pageIndex}: primary unusable ({primary.Error ?? "missing"}); loaded backup.");
            backup.FromBackup = true;
            return backup;
        }
        return !CanReplace(backup.State) || primary.NotFound ? backup : primary;
    }

    internal static bool CanReplace(PageFileState state) => state is PageFileState.Missing or PageFileState.Corrupt;

    internal PageLoadResult ReadCopy(int index, bool backup, bool materialize)
    {
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(Path.Combine(_root, FileName(index) + (backup ? ".bak" : "")));
        }
        catch (FileNotFoundException)
        {
            return PageLoadResult.Missing();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return new() { State = PageFileState.IoError, Error = e.Message };
        }
        return bytes is null ? PageLoadResult.Missing() : Parse(bytes, index, materialize);
    }

    // These bytes originate in a PageCache snapshot and are never mutated after capture.
    internal readonly record struct SaveSnapshot(int Index, byte[] Bytes, bool LoadedFromBackup);

    internal void StageNormalWrite(SaveSnapshot snapshot, List<StoreRedoJournal.Write> writes)
    {
        var (index, bytes, loadedFromBackup) = snapshot;
        var target = FileName(index);
        var primary = ReadCopy(index, backup: false, materialize: false);
        if (primary.Success && !loadedFromBackup)
        {
            var backup = ReadCopy(index, backup: true, materialize: false);
            if (backup.State == PageFileState.IoError)
                throw new IOException($"Page {index}: backup cannot be read: {backup.Error}");
            if (backup.State != PageFileState.UnsupportedVersion)
                writes.Add(new(target + ".bak", primary.Bytes));
        }
        else if (!primary.Success && !CanReplace(primary.State))
            throw new IOException($"Page {index}: refusing to overwrite {primary.State}: {primary.Error}");
        writes.Add(new(target, bytes));
    }

    internal PageFileInspection Inspect(int index)
    {
        var primary = ReadCopy(index, backup: false, materialize: false);
        if (!CanReplace(primary.State))
            return new(primary.State);
        var backup = ReadCopy(index, backup: true, materialize: false);
        if (backup.Success)
            return new(PageFileState.RecoverableFromBackup);
        return new(!CanReplace(backup.State) || primary.NotFound ? backup.State : primary.State);
    }

    internal PageSaveResult PromoteBackup(int index)
    {
        try
        {
            var journal = new StoreRedoJournal(_root);
            journal.Recover();
            var primary = ReadCopy(index, backup: false, materialize: false);
            var backup = ReadCopy(index, backup: true, materialize: true);
            if (!CanReplace(primary.State) || !backup.Success)
                return new() { Error = "Page copies do not permit backup promotion." };
            var summary = new PageSummary();
            summary.RebuildFrom(backup.Items);
            journal.Commit(new[] { new StoreRedoJournal.Write(FileName(index), UpgradeCopy(index, backup, summary)) });
            return new() { Success = true };
        }
        catch (Exception e) { return new() { Error = e.Message }; }
    }

    public IReadOnlyList<int> ListPages()
    {
        var indices = new SortedSet<int>();
        foreach (var path in Directory.EnumerateFiles(Path.Combine(_root, "pages"), "*" + Extension + "*"))
        {
            var name = Path.GetFileName(path);
            if (name.EndsWith(".bak", StringComparison.OrdinalIgnoreCase))
                name = name[..^4];
            if (name.Length == 12 && name.EndsWith(Extension, StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(name.AsSpan(0, 7), NumberStyles.None, CultureInfo.InvariantCulture, out var index) &&
                index < PageDirectory.MaxPageCount)
                indices.Add(index);
        }
        return new List<int>(indices);
    }

    public PageSaveResult WriteBlankPage(int index)
    {
        try
        {
            var journal = new StoreRedoJournal(_root);
            journal.Recover();
            var writes = new List<StoreRedoJournal.Write>();
            StageNormalWrite(new(index, Snapshot(index, null, new PageSummary()), false), writes);
            journal.Commit(writes);
            return new() { Success = true };
        }
        catch (Exception e) { return new() { Error = e.Message }; }
    }

    internal static byte[] Snapshot(int index, Item[] slots, PageSummary summary) =>
        Encode(index, summary, writer =>
        {
            writer.Write((ushort)summary.OccupiedStacks);
            if (slots is null)
                return;
            for (var slot = 0; slot < slots.Length; slot++)
            {
                var item = slots[slot];
                if (item is null || item.IsAir)
                    continue;
                using var stream = new MemoryStream();
                TagIO.ToStream(ItemIO.Save(item), stream, compress: false);
                var tagBytes = stream.ToArray();
                if (tagBytes.Length is <= 0 or > MaxItemTagBytes)
                    throw new InvalidDataException($"Page {index}: item tag length out of range at slot {slot}.");
                writer.Write((ushort)slot);
                writer.Write(tagBytes.Length);
                writer.Write(tagBytes);
            }
        });

    internal static byte[] UpgradeCopy(int index, PageLoadResult copy, PageSummary summary)
    {
        // Preserve the original item tag bytes without invoking mod save hooks during repair.
        using var source = new BinaryReader(new MemoryStream(copy.Bytes, writable: false));
        source.BaseStream.Position = 16;
        var quickLength = source.ReadInt32();
        var itemLength = source.ReadInt32();
        source.BaseStream.Position = HeaderSize + quickLength;
        var items = source.ReadBytes(itemLength);
        return Encode(index, summary, writer => writer.Write(items));
    }

    private static byte[] Encode(int index, PageSummary summary, Action<BinaryWriter> writeItems)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(Magic);
        writer.Write(CurrentVersion);
        writer.Write((ushort)0);
        writer.Write(index);
        writer.Write(0u);
        writer.Write(0);
        writer.Write(0);
        writer.Write(Guid.NewGuid().ToByteArray());
        WriteQuickView(writer, summary);
        var itemOffset = (int)stream.Position;
        writeItems(writer);
        var end = (int)stream.Position;
        stream.Position = 12;
        writer.Write(Crc32.Compute(stream.GetBuffer(), PayloadOffset, end - PayloadOffset));
        writer.Write(itemOffset - HeaderSize);
        writer.Write(end - itemOffset);
        return stream.ToArray();
    }

    private static PageLoadResult Parse(byte[] bytes, int index, bool materialize)
    {
        PageLoadResult Failure(PageFileState state, string error) => new() { State = state, Error = error, Bytes = bytes };
        Item[] items = null;
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            using var reader = new BinaryReader(stream, new UTF8Encoding(false, true));
            if (reader.ReadUInt32() != Magic)
                throw new InvalidDataException("Bad page magic.");
            var version = reader.ReadUInt16();
            if (version != CurrentVersion)
                return Failure(PageFileState.UnsupportedVersion, $"Unsupported page version {version}.");
            var flags = reader.ReadUInt16();
            if (flags != 0)
                return Failure(PageFileState.UnsupportedVersion, $"Unsupported page flags {flags}.");
            if (reader.ReadInt32() != index)
                throw new InvalidDataException("Page index mismatch.");
            var crc = reader.ReadUInt32();
            var quickLength = reader.ReadInt32();
            var itemLength = reader.ReadInt32();
            if (quickLength < 0 || itemLength < 2 || (long)HeaderSize + quickLength + itemLength != bytes.Length ||
                crc != Crc32.Compute(bytes, PayloadOffset, bytes.Length - PayloadOffset))
                throw new InvalidDataException("Page lengths or checksum do not match.");
            stream.Position = HeaderSize;
            ReadQuickView(reader);
            if (stream.Position != HeaderSize + quickLength)
                throw new InvalidDataException("Quick view section length mismatch.");
            var count = reader.ReadUInt16();
            if (count > ItemPage.SlotCount)
                throw new InvalidDataException("Invalid item count.");
            if (materialize)
                items = new Item[ItemPage.SlotCount];
            var seen = new bool[ItemPage.SlotCount];
            for (var i = 0; i < count; i++)
            {
                var slot = reader.ReadUInt16();
                var length = reader.ReadInt32();
                if (slot >= ItemPage.SlotCount || seen[slot] || length <= 0 || length > MaxItemTagBytes ||
                    length > stream.Length - stream.Position)
                    throw new InvalidDataException("Invalid item record.");
                seen[slot] = true;
                if (materialize)
                {
                    using var tagStream = new MemoryStream(bytes, (int)stream.Position, length, writable: false);
                    TagCompound tag;
                    try
                    {
                        tag = TagIO.FromStream(tagStream, compressed: false);
                        if (tagStream.Position != tagStream.Length)
                            throw new InvalidDataException("Trailing item tag bytes.");
                    }
                    catch (Exception e) when (e is IOException or FormatException or ArgumentException or InvalidOperationException or InvalidCastException)
                    {
                        throw new InvalidDataException($"Invalid item tag at slot {slot}.", e);
                    }
                    try
                    {
                        var item = ItemIO.Load(tag);
                        if (item is null || item.IsAir)
                            return Failure(PageFileState.ModError, $"Item loader did not restore slot {slot}.");
                        items[slot] = item;
                    }
                    catch (Exception e)
                    {
                        return Failure(PageFileState.ModError, $"Item loader failed at slot {slot}: {e.Message}");
                    }
                }
                stream.Position += length;
            }
            if (stream.Position != stream.Length)
                throw new InvalidDataException("Trailing page bytes.");
        }
        catch (Exception e) when (e is IOException or InvalidDataException or ArgumentException or FormatException)
        {
            return Failure(PageFileState.Corrupt, e.Message);
        }
        catch (Exception e)
        {
            return Failure(PageFileState.ModError, e.Message);
        }
        return new() { State = PageFileState.Valid, Bytes = bytes, Items = items };
    }

    private static void WriteQuickView(BinaryWriter writer, PageSummary summary)
    {
        foreach (var bits in summary.OccupiedBits) writer.Write(bits);
        foreach (var type in summary.SlotTypes) writer.Write(type);
        writer.Write((ushort)summary.Types.Count);
        foreach (var pair in summary.Types)
        {
            writer.Write(pair.Key);
            writer.Write(pair.Value.ExactCount);
            writer.Write(pair.Value.ExactFavorites);
        }
        writer.Write((ushort)summary.UnloadedTypes.Count);
        foreach (var pair in summary.UnloadedTypes)
        {
            writer.Write(pair.Key);
            writer.Write(pair.Value.ExactCount);
            writer.Write(pair.Value.ExactFavorites);
        }
    }

    private static PageSummary ReadQuickView(BinaryReader reader)
    {
        TypeStat ReadStat() => new()
        {
            ExactCount = reader.ReadInt64(),
            ExactFavorites = reader.ReadInt64()
        };
        var summary = new PageSummary();
        for (var i = 0; i < summary.OccupiedBits.Length; i++) summary.OccupiedBits[i] = reader.ReadUInt64();
        for (var i = 0; i < summary.SlotTypes.Length; i++) summary.SlotTypes[i] = reader.ReadInt32();
        var types = reader.ReadUInt16();
        if (types > ItemPage.SlotCount) throw new InvalidDataException("Invalid quick-view type count.");
        for (var i = 0; i < types; i++) summary.Types.Add(reader.ReadInt32(), ReadStat());
        var unloaded = reader.ReadUInt16();
        if (unloaded > ItemPage.SlotCount) throw new InvalidDataException("Invalid unloaded type count.");
        for (var i = 0; i < unloaded; i++) summary.UnloadedTypes.Add(reader.ReadString(), ReadStat());
        return summary;
    }
}

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
                value = (value & 1) != 0 ? 0xEDB88320u ^ (value >> 1) : value >> 1;
            table[i] = value;
        }
        return table;
    }

    public static uint Compute(byte[] data, int offset, int length)
    {
        var crc = 0xFFFFFFFFu;
        for (var i = offset; i < offset + length; i++)
            crc = Table[(crc ^ data[i]) & 0xFF] ^ (crc >> 8);
        return ~crc;
    }
}

internal sealed class PageLoadException : Exception
{
    public PageLoadException(string message) : base(message) { }
}
