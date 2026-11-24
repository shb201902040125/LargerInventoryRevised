using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using LargerInventory.Backend.Network;

namespace LargerInventory.Tests;

internal static partial class Program
{
    private static void RunOperationIdTests()
    {
        Run(nameof(OperationIds_ClassifyNewAndPersistedGuids));
        Run(nameof(OperationIds_DerivePersonalWarehouses));
        Run(nameof(OperationIds_StayOrderedThroughClockRollbackAndCounterRollover));
    }

    private static void OperationIds_ClassifyNewAndPersistedGuids()
    {
        var world = Guid.Parse("c5e4d590-62f7-11ea-bc55-0242ac130004");
        var generator = new WarehouseOperationId(Guid.Empty);
        var recoverable = generator.Next(world, Guid.Empty, true, 42);
        var ordinary = NetworkSystem.CreateWorldOperationId(world);
        var put = NetworkSystem.CreatePlayerOperationId(world, Guid.NewGuid());
        var legacy = Guid.NewGuid().ToByteArray();
        "LIORDv6!"u8.CopyTo(legacy);

        Expect(WarehouseOperationId.IsRecoverable(recoverable), "new journal IDs must support recovery");
        Expect(WarehouseOperationId.IsRecoverable(new Guid(legacy)), "persisted journal IDs must retain recovery");
        Expect(!WarehouseOperationId.IsRecoverable(ordinary) && !WarehouseOperationId.IsRecoverable(put) &&
            !WarehouseOperationId.IsRecoverable(Guid.NewGuid()), "GUID-only IDs must remain valid for legacy and authorized APIs");
    }

    private static void OperationIds_DerivePersonalWarehouses()
    {
        var world = Guid.Parse("c5e4d590-62f7-11ea-bc55-0242ac130004");
        var otherWorld = Guid.Parse("c5e4d590-62f7-11ea-bc55-0242ac130005");
        var character = Guid.Parse("30f7457d-dd78-47bf-b417-c401a7fcc32c");
        var otherCharacter = Guid.Parse("30f7457d-dd78-47bf-b417-c401a7fcc32d");
        var personal = WarehouseOperationId.PersonalWarehouse(world, character);
        Expect(personal == WarehouseOperationId.PersonalWarehouse(world, character) && personal != world &&
            personal != WarehouseOperationId.PersonalWarehouse(otherWorld, character) &&
            personal != WarehouseOperationId.PersonalWarehouse(world, otherCharacter),
            "personal warehouse identity must be stable and include both world and character");

        var worldId = new WarehouseOperationId(Guid.Empty).Next(world, Guid.Empty, true, 42);
        var personalId = new WarehouseOperationId(Guid.Empty).Next(personal, Guid.Empty, true, 42);
        var otherClientId = new WarehouseOperationId(otherCharacter).Next(world, Guid.Empty, true, 42);
        Expect(worldId != personalId && worldId != otherClientId, "the node must incorporate the warehouse and distinct process nonce");
    }

    private static void OperationIds_StayOrderedThroughClockRollbackAndCounterRollover()
    {
        var generator = new WarehouseOperationId(Guid.Empty);
        var warehouse = Guid.Parse("c5e4d590-62f7-11ea-bc55-0242ac130004");
        var seen = new HashSet<Guid>();
        var previous = Guid.Empty;
        for (int i = 0; i < 4200; i++)
        {
            var id = generator.Next(warehouse, Guid.Empty, true, i == 0 ? 6000 : 5000);
            Expect(seen.Add(id) && id.CompareTo(previous) > 0, "counter rollover must advance logical time without reusing an ID");
            previous = id;
        }
        var bytes = previous.ToByteArray(bigEndian: true);
        Expect((BinaryPrimitives.ReadUInt64BigEndian(bytes) >> 16) == 6001, "UUID must carry the advanced logical millisecond");
    }
}
