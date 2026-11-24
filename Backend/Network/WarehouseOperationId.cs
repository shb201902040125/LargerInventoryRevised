using System;
using System.Buffers.Binary;
using System.Security.Cryptography;

namespace LargerInventory.Backend.Network;

internal sealed class WarehouseOperationId
{
    private const int MaxCounter = (1 << 12) - 1;
    private const long MaxTimestamp = (1L << 48) - 1;
    private const ulong RecoverableNamespace = 0x02D4C149;
    private const ulong OrdinaryNamespace = 0x02D4C14A;
    private const ulong Variant = 0x8000000000000000UL;
    private const ulong NodeMask = (1UL << 36) - 1;
    private readonly object _gate = new();
    private readonly Guid _processNonce;
    private long _lastTimestamp = -1;
    private int _counter;

    internal WarehouseOperationId(Guid processNonce) => _processNonce = processNonce;

    private ulong DeriveNode(Guid warehouse, Guid lineage)
    {
        Span<byte> identity = stackalloc byte[48];
        WriteGuid(warehouse, identity);
        WriteGuid(lineage, identity[16..]);
        WriteGuid(_processNonce, identity[32..]);
        Span<byte> digest = stackalloc byte[32];
        SHA256.HashData(identity, digest);
        return BinaryPrimitives.ReadUInt64BigEndian(digest) & NodeMask;
    }

    internal static Guid PersonalWarehouse(Guid world, Guid character)
    {
        var domain = "LargerInventory/PersonalWarehouse/v1"u8;
        Span<byte> identity = stackalloc byte[domain.Length + 32];
        domain.CopyTo(identity);
        WriteGuid(world, identity[domain.Length..]);
        WriteGuid(character, identity[(domain.Length + 16)..]);
        Span<byte> digest = stackalloc byte[32];
        SHA256.HashData(identity, digest);
        digest[6] = (byte)((digest[6] & 0x0f) | 0x80);
        digest[8] = (byte)((digest[8] & 0x3f) | 0x80);
        return new Guid(digest[..16], bigEndian: true);
    }

    internal Guid Next(Guid warehouse, Guid lineage, bool recoverable, long utcMilliseconds)
    {
        if (warehouse == Guid.Empty)
            throw new ArgumentException("A warehouse UUID is required.", nameof(warehouse));
        if (utcMilliseconds < 0 || utcMilliseconds > MaxTimestamp)
            throw new ArgumentOutOfRangeException(nameof(utcMilliseconds));

        ulong node = DeriveNode(warehouse, lineage);
        ulong operationNamespace = recoverable ? RecoverableNamespace : OrdinaryNamespace;
        lock (_gate)
        {
            // Use logical time during clock rollback or when the millisecond counter fills.
            if (utcMilliseconds > _lastTimestamp)
            {
                _lastTimestamp = utcMilliseconds;
                _counter = 0;
            }
            else if (_counter == MaxCounter)
            {
                if (_lastTimestamp == MaxTimestamp)
                    throw new InvalidOperationException("Operation ID timestamp exhausted.");
                ++_lastTimestamp;
                _counter = 0;
            }
            else
            {
                ++_counter;
            }

            // UUIDv8 layout: 48-bit logical time, 12-bit counter, 26-bit marker, 36-bit warehouse node.
            Span<byte> bytes = stackalloc byte[16];
            BinaryPrimitives.WriteUInt64BigEndian(bytes, ((ulong)_lastTimestamp << 16) | 0x8000UL | (uint)_counter);
            BinaryPrimitives.WriteUInt64BigEndian(bytes[8..], Variant | (operationNamespace << 36) | node);
            return new Guid(bytes, bigEndian: true);
        }
    }

    internal static bool IsRecoverable(Guid id)
    {
        Span<byte> bytes = stackalloc byte[16];
        // Existing sidecar and ledger GUIDs used the little-endian .NET byte layout.
        id.TryWriteBytes(bytes);
        if (bytes[..8].SequenceEqual("LIORDv6!"u8))
            return true;

        WriteGuid(id, bytes);
        return (bytes[6] & 0xf0) == 0x80 &&
            BinaryPrimitives.ReadUInt64BigEndian(bytes[8..]) >> 36 == ((Variant >> 36) | RecoverableNamespace);
    }

    private static void WriteGuid(Guid id, Span<byte> bytes) => id.TryWriteBytes(bytes, bigEndian: true, out _);
}

public static partial class NetworkSystem
{
    private static readonly WarehouseOperationId OperationIds = new(Guid.NewGuid());

    /// <summary>Creates a world warehouse operation ID for the GUID-only APIs, including authorized Put.</summary>
    public static Guid CreateWorldOperationId(Guid worldId) =>
        OperationIds.Next(worldId, Guid.Empty, recoverable: false, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

    /// <summary>Creates an operation ID for a character's personal warehouse on the given world.</summary>
    public static Guid CreatePlayerOperationId(Guid worldId, Guid characterId)
    {
        if (worldId == Guid.Empty)
            throw new ArgumentException("A world UUID is required.", nameof(worldId));
        if (characterId == Guid.Empty)
            throw new ArgumentException("A character UUID is required.", nameof(characterId));

        return CreateWorldOperationId(WarehouseOperationId.PersonalWarehouse(worldId, characterId));
    }

    private static bool TryCreateClientOperationId(Scope scope, bool recoverable, out Guid id)
    {
        id = default;
        if (_client is not { } session || recoverable && session.JournalLineage == Guid.Empty)
            return false;

        var warehouse = scope == Scope.World ? session.World : WarehouseOperationId.PersonalWarehouse(session.World, session.Player);
        id = OperationIds.Next(warehouse, session.JournalLineage, recoverable, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        return true;
    }

    private static bool IsRecoverableId(Guid id) => WarehouseOperationId.IsRecoverable(id);
}
