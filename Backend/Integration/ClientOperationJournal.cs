using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using LargerInventory.Backend.Storage;

namespace LargerInventory.Backend.Integration;

// Stores sequenced operations and the save lineage used for inventory grants.
// Caller-supplied GUIDs use the same transport without a journaled sequence.
internal sealed class ClientOperationJournal
{
    internal readonly record struct Entry(StoreScope Scope, Guid OperationId, long Sequence);

    private sealed record State(Guid Lineage, long LastSequence, Dictionary<Guid, Entry> Pending);

    private const uint Magic = 0x314A4F4C; // LOJ1
    private const int HeaderSize = 4 + 16 + 16 + 16 + 8 + 4;
    private const int EntrySize = 1 + 16 + 8;
    private const int HashSize = 32;
    internal const int MaxFileEntries = 4096;

    private readonly object _gate = new();
    private readonly string _path;
    private readonly Guid _characterId;
    private readonly Guid _worldId;
    private State _state;

    internal Guid Lineage { get; }

    private ClientOperationJournal(string path, Guid characterId, Guid worldId, State state)
    {
        _path = path;
        _characterId = characterId;
        _worldId = worldId;
        _state = state;
        Lineage = state.Lineage;
    }

    // A copied character or local/cloud migration opens a separate lineage. Recovery requiring
    // the original lineage is therefore limited to the original save path on this install.
    internal static bool TryOpen(string saveRoot, Guid characterId, Guid worldId, string playerPath, bool cloud,
        out ClientOperationJournal journal)
    {
        journal = null;
        if (string.IsNullOrWhiteSpace(playerPath))
        {
            return false;
        }

        try
        {
            string path = cloud ? playerPath.Replace('\\', '/') : Path.GetFullPath(playerPath);
            if (!cloud && OperatingSystem.IsWindows())
            {
                path = path.ToUpperInvariant();
            }

            string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path)));
            return TryOpenCore(saveRoot, characterId, worldId, (cloud ? "cloud-" : "local-") + hash, out journal);
        }
        catch (Exception ex) when (IsStorageFailure(ex))
        {
            return false;
        }
    }

    private static bool TryOpenCore(string saveRoot, Guid characterId, Guid worldId, string provenance,
        out ClientOperationJournal journal)
    {
        journal = null;
        if (string.IsNullOrWhiteSpace(saveRoot) || characterId == Guid.Empty || worldId == Guid.Empty)
        {
            return false;
        }

        try
        {
            string directory = Path.Combine(Path.GetFullPath(saveRoot), "LargerInventory", "ClientOperations", $"{characterId:N}");
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, $"{worldId:N}-{provenance}.journal");
            using var fileLock = LockFile(path);
            State state;
            try
            {
                state = ReadState(path, characterId, worldId);
            }
            catch (FileNotFoundException)
            {
                state = new State(Guid.NewGuid(), 0, new Dictionary<Guid, Entry>());
                Publish(path, characterId, worldId, state, overwrite: false);
            }

            journal = new ClientOperationJournal(path, characterId, worldId, state);
            return true;
        }
        catch (Exception ex) when (IsStorageFailure(ex))
        {
            return false;
        }
    }

    internal Entry[] GetPending()
    {
        lock (_gate)
        {
            return OrderedEntries(_state);
        }
    }

    internal bool TryGet(Guid operationId, out Entry entry)
    {
        lock (_gate)
        {
            return _state.Pending.TryGetValue(operationId, out entry);
        }
    }

    internal bool TryStage(StoreScope scope, Guid operationId, long serverHighWatermark, out Entry entry)
    {
        entry = default;
        if (!IsValidScope(scope) || operationId == Guid.Empty || serverHighWatermark < 0)
        {
            return false;
        }

        lock (_gate)
        {
            try
            {
                using var fileLock = LockFile(_path);
                Reload();
                if (_state.Pending.TryGetValue(operationId, out var existing))
                {
                    if (existing.Scope != scope)
                    {
                        return false;
                    }

                    entry = existing;
                    return true;
                }

                long floor = Math.Max(_state.LastSequence, serverHighWatermark);
                if (floor == long.MaxValue || _state.Pending.Count >= StoreConfig.Runtime.MaxClientPendingOperations)
                {
                    return false;
                }

                var staged = new Entry(scope, operationId, floor + 1);
                var pending = new Dictionary<Guid, Entry>(_state.Pending) { [operationId] = staged };
                Commit(new State(Lineage, staged.Sequence, pending));
                entry = staged;
                return true;
            }
            catch (Exception ex) when (IsStorageFailure(ex))
            {
                return false;
            }
        }
    }

    // Call only after the server has definitively resolved the operation and local delivery
    // is safe. Retired GUIDs are discarded, so callers must never stage them again.
    internal bool TryConfirmRetired(Entry entry)
    {
        if (!IsValidScope(entry.Scope) || entry.OperationId == Guid.Empty || entry.Sequence <= 0)
        {
            return false;
        }

        lock (_gate)
        {
            try
            {
                using var fileLock = LockFile(_path);
                Reload();
                if (!_state.Pending.TryGetValue(entry.OperationId, out var existing) || existing != entry)
                {
                    return false;
                }

                var pending = new Dictionary<Guid, Entry>(_state.Pending);
                pending.Remove(entry.OperationId);
                Commit(new State(Lineage, _state.LastSequence, pending));
                return true;
            }
            catch (Exception ex) when (IsStorageFailure(ex))
            {
                return false;
            }
        }
    }

    private void Reload()
    {
        var state = ReadState(_path, _characterId, _worldId);
        if (state.Lineage != Lineage || state.LastSequence < _state.LastSequence)
        {
            throw new InvalidDataException("Client operation journal was replaced or rolled back.");
        }

        _state = state;
    }

    private void Commit(State state)
    {
        Publish(_path, _characterId, _worldId, state, overwrite: true);
        _state = state;
    }

    private static FileStream LockFile(string path) =>
        new(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

    private static State ReadState(string path, Guid characterId, Guid worldId)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
        long length = file.Length;
        if (length < HeaderSize + HashSize || length > HeaderSize + MaxFileEntries * EntrySize + HashSize)
        {
            throw new InvalidDataException("Invalid client operation journal length.");
        }

        byte[] payload = new byte[(int)length - HashSize];
        byte[] hash = new byte[HashSize];
        file.ReadExactly(payload);
        file.ReadExactly(hash);
        if (!SHA256.HashData(payload).AsSpan().SequenceEqual(hash))
        {
            throw new InvalidDataException("Invalid client operation journal checksum.");
        }

        using var reader = new BinaryReader(new MemoryStream(payload, writable: false));
        if (reader.ReadUInt32() != Magic || ReadGuid(reader) != characterId || ReadGuid(reader) != worldId)
        {
            throw new InvalidDataException("Unknown client operation journal format or identity.");
        }

        Guid lineage = ReadGuid(reader);
        long lastSequence = reader.ReadInt64();
        int count = reader.ReadInt32();
        if (lineage == Guid.Empty || lastSequence < 0 || count < 0 || count > MaxFileEntries ||
            payload.Length != HeaderSize + count * EntrySize)
        {
            throw new InvalidDataException("Invalid client operation journal header.");
        }

        var pending = new Dictionary<Guid, Entry>(count);
        long previous = 0;
        for (int i = 0; i < count; ++i)
        {
            var entry = new Entry((StoreScope)reader.ReadByte(), ReadGuid(reader), reader.ReadInt64());
            if (!IsValidScope(entry.Scope) || entry.OperationId == Guid.Empty || entry.Sequence <= previous ||
                entry.Sequence > lastSequence || !pending.TryAdd(entry.OperationId, entry))
            {
                throw new InvalidDataException("Invalid pending client operation.");
            }

            previous = entry.Sequence;
        }

        return new State(lineage, lastSequence, pending);
    }

    private static void Publish(string path, Guid characterId, Guid worldId, State state, bool overwrite)
    {
        using var data = new MemoryStream(HeaderSize + state.Pending.Count * EntrySize);
        using var writer = new BinaryWriter(data);
        writer.Write(Magic);
        writer.Write(characterId.ToByteArray());
        writer.Write(worldId.ToByteArray());
        writer.Write(state.Lineage.ToByteArray());
        writer.Write(state.LastSequence);
        writer.Write(state.Pending.Count);
        foreach (var entry in OrderedEntries(state))
        {
            writer.Write((byte)entry.Scope);
            writer.Write(entry.OperationId.ToByteArray());
            writer.Write(entry.Sequence);
        }

        writer.Flush();
        var payload = data.GetBuffer().AsSpan(0, (int)data.Length);
        string temporary = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            // Flush the complete image before replacing the live file in the same directory.
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                file.Write(payload);
                file.Write(SHA256.HashData(payload));
                file.Flush(flushToDisk: true);
            }

            File.Move(temporary, path, overwrite);
        }
        catch
        {
            try { File.Delete(temporary); }
            catch (Exception ex) when (IsStorageFailure(ex)) { }
            throw;
        }
    }

    private static Entry[] OrderedEntries(State state) => state.Pending.Values.OrderBy(entry => entry.Sequence).ToArray();

    private static Guid ReadGuid(BinaryReader reader) => new(reader.ReadBytes(16));

    private static bool IsValidScope(StoreScope scope) => scope is StoreScope.Player or StoreScope.World;

    private static bool IsStorageFailure(Exception ex) =>
        ex is IOException or InvalidDataException or UnauthorizedAccessException or SecurityException or ArgumentException or NotSupportedException;
}
