using System;
using System.Collections.Generic;
using System.IO;

namespace LargerInventory.Backend.Network;

/// <summary>Per-store durable transaction results and per-player retirement floors.</summary>
internal sealed class TransactionLedger
{
    private const int Version = 4;
    internal const int FingerprintLength = 32;
    private const int HeaderBytes = sizeof(int) * 3;
    private const int EntryBytes = 16 + 16 + FingerprintLength + 1 + 1 + 1 + sizeof(int);
    private const int SequenceBytes = sizeof(long);
    private const int FloorBytes = 16 + SequenceBytes;
    private const int MaxBytes = 32 * 1024 * 1024;
    private const int MaxEntries = 32_768;

    internal readonly record struct Key(Guid Player, Guid Request);
    internal sealed record Entry(byte[] Fingerprint, bool Deliver, bool Acknowledged, byte[] Result,
        long? Sequence = null)
    {
        internal bool Matches(byte[] fingerprint) => Fingerprint is not null && fingerprint is not null &&
            Fingerprint.AsSpan().SequenceEqual(fingerprint);
    }

    internal enum LookupStatus { Fresh, Retained, Retired, Conflict }

    internal sealed class Compaction
    {
        private readonly byte[] _contents;

        internal Compaction(TransactionLedger owner, long revision, Guid player, long floor, Key[] removed, byte[] contents)
        {
            Owner = owner;
            Revision = revision;
            Player = player;
            Floor = floor;
            Removed = Array.AsReadOnly(removed);
            _contents = contents;
        }

        internal TransactionLedger Owner { get; }
        internal long Revision { get; }
        internal Guid Player { get; }
        internal long Floor { get; }
        internal IReadOnlyList<Key> Removed { get; }
        internal byte[] Contents => (byte[])_contents.Clone();
        internal int Length => _contents.Length;
    }

    private readonly Dictionary<Key, Entry> _entries = new();
    private readonly Dictionary<Guid, long> _retiredThrough = new();
    private readonly Dictionary<Guid, long> _highestSequences = new();
    private int _size = HeaderBytes;
    private long _revision;

    internal TransactionLedger(byte[] contents)
    {
        if (contents is null)
        {
            return;
        }

        if (contents.Length > MaxBytes)
        {
            throw new InvalidDataException("Transaction ledger is too large.");
        }

        using var reader = new BinaryReader(new MemoryStream(contents, writable: false));
        var version = reader.ReadInt32();
        if (version != Version)
        {
            throw new InvalidDataException("Unknown transaction ledger version.");
        }

        var count = reader.ReadInt32();
        if (count < 0 || count > MaxEntries)
        {
            throw new InvalidDataException("Invalid transaction ledger entry count.");
        }

        var floorCount = reader.ReadInt32();
        var remaining = reader.BaseStream.Length - reader.BaseStream.Position;
        if (floorCount < 0 || (long)floorCount * FloorBytes + (long)count * (EntryBytes + 1) > remaining)
        {
            throw new InvalidDataException("Invalid transaction ledger floor count.");
        }

        for (var i = 0; i < floorCount; i++)
        {
            var player = ReadGuid(reader);
            var floor = reader.ReadInt64();
            if (player == Guid.Empty || floor <= 0 || !_retiredThrough.TryAdd(player, floor))
            {
                throw new InvalidDataException("Invalid transaction ledger retirement floor.");
            }

            _highestSequences.Add(player, floor);
        }

        var sequences = new HashSet<(Guid Player, long Sequence)>();

        for (var i = 0; i < count; i++)
        {
            var key = new Key(ReadGuid(reader), ReadGuid(reader));
            var fingerprint = reader.ReadBytes(FingerprintLength);
            if (fingerprint.Length != FingerprintLength)
            {
                throw new InvalidDataException("Incomplete transaction fingerprint.");
            }

            var deliver = reader.ReadBoolean();
            var acknowledged = reader.ReadBoolean();
            var serverDelivery = reader.ReadBoolean();
            var length = reader.ReadInt32();
            long? sequence = null;
            // Version 4 uses a negative result length to mark the optional sequence before the result.
            if (length is < 0 and > int.MinValue)
            {
                length = -length;
                sequence = reader.ReadInt64();
                if (sequence.Value <= GetRetiredThrough(key.Player) || !sequences.Add((key.Player, sequence.Value)))
                {
                    throw new InvalidDataException("Invalid or duplicate transaction sequence.");
                }

                _highestSequences[key.Player] = Math.Max(GetHighestSequence(key.Player), sequence.Value);
            }

            if (key.Player == Guid.Empty || key.Request == Guid.Empty || (acknowledged && !deliver) || deliver != serverDelivery ||
                length <= 0 || length > NetworkSystem.MaxResultBytes ||
                length > reader.BaseStream.Length - reader.BaseStream.Position)
            {
                throw new InvalidDataException("Invalid transaction ledger entry.");
            }

            if (!_entries.TryAdd(key, new Entry(fingerprint, deliver, acknowledged, reader.ReadBytes(length), sequence)))
            {
                throw new InvalidDataException("Duplicate transaction ledger entry.");
            }
        }

        if (reader.BaseStream.Position != reader.BaseStream.Length)
        {
            throw new InvalidDataException("Unexpected data at end of transaction ledger.");
        }

        _size = contents.Length;
    }

    // GUID-only recovery and receipts may read retained entries. Mutation resends must use Lookup.
    internal bool TryGet(Key key, out Entry entry) => _entries.TryGetValue(key, out entry);

    internal bool HasPendingDelivery(Guid player, Guid exceptRequest = default)
    {
        foreach (var pair in _entries)
        {
            if (pair.Key.Player == player && pair.Key.Request != exceptRequest && IsPendingDelivery(pair.Value))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsPendingDelivery(Entry entry) => entry.Deliver && !entry.Acknowledged;

    internal long GetRetiredThrough(Guid player) => _retiredThrough.GetValueOrDefault(player);

    // Includes the floor, even when compaction left no retained entries for this player.
    internal long GetHighestSequence(Guid player) => _highestSequences.GetValueOrDefault(player);

    // Retired entries no longer bind their request GUID. The wire protocol must resend the original sequence.
    // For retained mutations, the caller also checks the fingerprint before replaying the result.
    internal LookupStatus Lookup(Key key, long? sequence, out Entry entry)
    {
        entry = null;
        if (key.Player == Guid.Empty || key.Request == Guid.Empty || sequence is <= 0)
        {
            return LookupStatus.Conflict;
        }

        if (_entries.TryGetValue(key, out var retained))
        {
            if (retained.Sequence != sequence)
            {
                return LookupStatus.Conflict;
            }

            entry = retained;
            return LookupStatus.Retained;
        }

        if (sequence is long value)
        {
            if (value <= GetRetiredThrough(key.Player))
            {
                return LookupStatus.Retired;
            }

            if (value <= GetHighestSequence(key.Player))
            {
                return LookupStatus.Conflict;
            }
        }

        return LookupStatus.Fresh;
    }

    internal bool HasRoom => _entries.Count < MaxEntries && _size + EntryBytes < MaxBytes;

    // Projection only. Publish the in-memory change after the store's commit marker is durable.
    internal byte[] Encode(Key key, Entry entry)
    {
        var (length, replacing) = ProjectEntry(key, entry);
        using var stream = new MemoryStream(length);
        using var writer = new BinaryWriter(stream);
        WriteHeader(writer, _entries.Count + (replacing ? 0 : 1));
        foreach (var pair in _entries)
        {
            WriteEntry(writer, pair.Key, pair.Key == key ? entry : pair.Value);
        }

        if (!replacing)
        {
            WriteEntry(writer, key, entry);
        }

        return stream.ToArray();
    }

    internal void Publish(Key key, Entry entry)
    {
        var (length, _) = ProjectEntry(key, entry);
        _entries[key] = entry;
        if (entry.Sequence is long sequence)
        {
            _highestSequences[key.Player] = Math.Max(GetHighestSequence(key.Player), sequence);
        }

        _size = length;
        _revision++;
    }

    // The caller supplies an authenticated cumulative client acknowledgement: results through this sequence can
    // be forgotten. A pending delivery blocks retirement even if the client claims to be done with its result.
    // Serialize prepare, durable commit of Contents as-is, and Publish with ALL other ledger writes for this store.
    internal bool TryPrepareCompaction(Guid player, long acknowledgedThrough, out Compaction compaction)
    {
        compaction = null;
        if (player == Guid.Empty || acknowledgedThrough <= GetRetiredThrough(player) ||
            acknowledgedThrough > GetHighestSequence(player))
        {
            return false;
        }

        var removed = new HashSet<Key>();
        var length = _size + (_retiredThrough.ContainsKey(player) ? 0 : FloorBytes);
        foreach (var pair in _entries)
        {
            var entry = pair.Value;
            // Keep GUID-only entries indefinitely. No sequence floor can identify
            // their resends, and delivery recovery still needs their original result.
            if (pair.Key.Player != player || entry.Sequence is not long sequence || sequence > acknowledgedThrough)
            {
                continue;
            }

            if (IsPendingDelivery(entry))
            {
                return false;
            }

            removed.Add(pair.Key);
            length -= EntryBytes + SequenceBytes + entry.Result.Length;
        }

        if (length > MaxBytes)
        {
            return false;
        }

        using var stream = new MemoryStream(length);
        using var writer = new BinaryWriter(stream);
        WriteHeader(writer, _entries.Count - removed.Count, floorPlayer: player, floor: acknowledgedThrough);
        foreach (var pair in _entries)
        {
            if (!removed.Contains(pair.Key))
            {
                WriteEntry(writer, pair.Key, pair.Value);
            }
        }

        var removedKeys = new Key[removed.Count];
        removed.CopyTo(removedKeys);
        compaction = new Compaction(this, _revision, player, acknowledgedThrough, removedKeys, stream.ToArray());
        return true;
    }

    // Call only after a successful durable commit. On failure, discard the projection and prepare again for retry.
    internal void Publish(Compaction compaction)
    {
        if (compaction is null || compaction.Owner != this || compaction.Revision != _revision)
        {
            throw new InvalidOperationException("Compaction does not match the current transaction ledger.");
        }

        _retiredThrough[compaction.Player] = compaction.Floor;
        foreach (var key in compaction.Removed)
        {
            _entries.Remove(key);
        }

        _size = compaction.Length;
        _revision++;
    }

    private (int Length, bool Replacing) ProjectEntry(Key key, Entry entry)
    {
        if (entry is null || entry.Fingerprint?.Length != FingerprintLength ||
            entry.Acknowledged && !entry.Deliver || entry.Result is null ||
            entry.Result.Length <= 0 || entry.Result.Length > NetworkSystem.MaxResultBytes)
        {
            throw new InvalidDataException("Invalid transaction ledger entry.");
        }

        var lookup = Lookup(key, entry.Sequence, out var previous);
        if (lookup is LookupStatus.Conflict or LookupStatus.Retired)
        {
            throw new InvalidDataException("Transaction sequence conflicts with the ledger or was retired.");
        }

        var replacing = lookup == LookupStatus.Retained;
        var length = _size + entry.Result.Length;
        if (replacing)
        {
            length -= previous.Result.Length;
        }
        else
        {
            length += EntryBytes + (entry.Sequence.HasValue ? sizeof(long) : 0);
        }

        if (length > MaxBytes || !replacing && _entries.Count >= MaxEntries)
        {
            throw new InvalidDataException("Transaction ledger capacity reached.");
        }

        return (length, replacing);
    }

    private void WriteHeader(BinaryWriter writer, int count, Guid? floorPlayer = null, long floor = 0)
    {
        writer.Write(Version);
        writer.Write(count);

        var addingFloor = floorPlayer is Guid player && !_retiredThrough.ContainsKey(player);
        writer.Write(_retiredThrough.Count + (addingFloor ? 1 : 0));
        foreach (var pair in _retiredThrough)
        {
            writer.Write(pair.Key.ToByteArray());
            writer.Write(pair.Key == floorPlayer ? floor : pair.Value);
        }

        if (addingFloor)
        {
            writer.Write(floorPlayer.Value.ToByteArray());
            writer.Write(floor);
        }
    }

    private static void WriteEntry(BinaryWriter writer, Key key, Entry entry)
    {
        writer.Write(key.Player.ToByteArray());
        writer.Write(key.Request.ToByteArray());
        writer.Write(entry.Fingerprint);
        writer.Write(entry.Deliver);
        writer.Write(entry.Acknowledged);
        writer.Write(entry.Deliver); // The v4 server-delivery marker is required for all deliveries.
        writer.Write(entry.Sequence.HasValue ? -entry.Result.Length : entry.Result.Length);
        if (entry.Sequence is long sequence)
        {
            writer.Write(sequence);
        }

        writer.Write(entry.Result);
    }

    private static Guid ReadGuid(BinaryReader reader)
    {
        var bytes = reader.ReadBytes(16);
        if (bytes.Length != 16)
        {
            throw new InvalidDataException("Incomplete transaction ledger identity.");
        }

        return new Guid(bytes);
    }
}
