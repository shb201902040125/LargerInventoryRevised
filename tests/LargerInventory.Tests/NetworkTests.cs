using System;
using System.IO;
using LargerInventory.Backend.Commands;
using LargerInventory.Backend.Network;

namespace LargerInventory.Tests;

internal static partial class Program
{
    private static void RunNetworkTests()
    {
        Run(nameof(NetworkLedger_ReloadRejectsChangedRequests));
        Run(nameof(NetworkLedger_RejectsOldVersions));
        Run(nameof(NetworkLedger_ServerDeliveryKeepsCompletedStateAfterReload));
        Run(nameof(NetworkReceipt_RequiresOwnerConnectionAndCompletedTransfer));
        Run(nameof(NetworkLedger_RetirementSurvivesReloadAndKeepsPendingDelivery));
        Run(nameof(NetworkDelivery_SerializesRecoveryItemsOnlyForServer));
        Run(nameof(NetworkDelivery_RejectsGenerationZeroGrants));
        Run(nameof(NetworkRecovery_DistinguishesCommittedResultsFromLookupFailures));
    }

    private static void NetworkLedger_ReloadRejectsChangedRequests()
    {
        var request = new TransactionLedger.Key(Guid.NewGuid(), Guid.NewGuid());
        var original = NetworkSystem.CreateRequestFingerprint(NetworkSystem.Op.Mutate, CommandFactory.Move(1, 2));
        var ledger = new TransactionLedger(null);
        var persisted = ledger.Encode(request, new TransactionLedger.Entry(original, false, false, new byte[] { 1 }));
        Expect(BitConverter.ToInt32(persisted) == 4 && BitConverter.ToInt32(persisted, 8) == 0,
            "GUID-only transactions must use the current ledger header");
        var reloaded = new TransactionLedger(persisted);
        Expect(reloaded.TryGet(request, out var entry), "result must survive reload");
        Expect(entry.Matches(NetworkSystem.CreateRequestFingerprint(NetworkSystem.Op.Mutate, CommandFactory.Move(1, 2))),
            "identical replay must match");
        Expect(!entry.Matches(NetworkSystem.CreateRequestFingerprint(NetworkSystem.Op.Mutate, CommandFactory.Move(2, 1))),
            "changing move slots must conflict");
        Expect(!entry.Matches(NetworkSystem.CreateRequestFingerprint(NetworkSystem.Op.Put)), "changing action must conflict");
        Expect(!reloaded.TryGet(request with { Player = Guid.NewGuid() }, out _), "other UUID must have its own result key");

        var fill = new TransactionLedger.Entry(NetworkSystem.CreateRequestFingerprint(NetworkSystem.Op.DebugFill, type: 1, count: 4),
            false, false, new byte[] { 1 });
        Expect(!fill.Matches(NetworkSystem.CreateRequestFingerprint(NetworkSystem.Op.DebugFill, type: 2, count: 4)),
            "changing fill type must conflict");
        Expect(!fill.Matches(NetworkSystem.CreateRequestFingerprint(NetworkSystem.Op.DebugFill, type: 1, count: 5)),
            "changing fill amount must conflict");
        var take = new TransactionLedger.Entry(NetworkSystem.CreateRequestFingerprint(NetworkSystem.Op.Mutate, CommandFactory.Take(1, 4, true)),
            true, false, new byte[] { 1 });
        Expect(!take.Matches(NetworkSystem.CreateRequestFingerprint(NetworkSystem.Op.Mutate, CommandFactory.Take(1, 4, false))),
            "changing the favorite policy must conflict");
    }

    private static void NetworkLedger_RejectsOldVersions()
    {
        for (var version = 1; version <= 3; version++)
        {
            using var bytes = new MemoryStream();
            using var writer = new BinaryWriter(bytes);
            writer.Write(version);
            writer.Write(0);
            var rejected = false;
            try { _ = new TransactionLedger(bytes.ToArray()); }
            catch (InvalidDataException) { rejected = true; }
            Expect(rejected, $"obsolete ledger version {version} must be rejected");
        }
    }

    private static void NetworkLedger_ServerDeliveryKeepsCompletedStateAfterReload()
    {
        var key = new TransactionLedger.Key(Guid.NewGuid(), Guid.NewGuid());
        var fingerprint = NetworkSystem.CreateRequestFingerprint(NetworkSystem.Op.Mutate, CommandFactory.Take(1, 1));
        var ledger = new TransactionLedger(null);
        var lineage = Guid.NewGuid();
        var pending = new ServerDelivery { Status = CommandStatus.Partial, OwnerLineage = lineage };
        var initial = new TransactionLedger.Entry(fingerprint, true, false, pending.Write());
        ledger = new TransactionLedger(ledger.Encode(key, initial));
        Expect(ledger.TryGet(key, out var entry) && entry.Deliver && !entry.Acknowledged,
            "new delivery must remain server-owned when reloaded");

        var completed = new ServerDelivery { Status = CommandStatus.Partial, Planned = true, OwnerLineage = lineage };
        ledger = new TransactionLedger(ledger.Encode(key, entry with { Result = completed.Write(), Acknowledged = true }));
        Expect(ledger.TryGet(key, out var replay) && replay.Deliver && replay.Acknowledged && replay.Matches(fingerprint),
            "completed server delivery must remain complete after reload and match a retry");
        var restored = ServerDelivery.Read(replay.Result);
        Expect(restored.Planned && restored.Grants.Count == 0 && restored.Remaining.Count == 0 && restored.Status == CommandStatus.Partial,
            "completed delivery must keep its result with nothing left to grant or drop");
    }

    private static void NetworkLedger_RetirementSurvivesReloadAndKeepsPendingDelivery()
    {
        var player = Guid.NewGuid();
        var completed = new TransactionLedger.Key(player, Guid.NewGuid());
        var pending = new TransactionLedger.Key(player, Guid.NewGuid());
        var guidOnly = new TransactionLedger.Key(player, Guid.NewGuid());
        var ledger = new TransactionLedger(null);
        var result = new TransactionLedger.Entry(NetworkSystem.CreateRequestFingerprint(NetworkSystem.Op.Put), false, false, new byte[] { 1 });
        ledger.Publish(guidOnly, result);
        ledger.Publish(completed, result with { Sequence = 3 });
        ledger.Publish(pending, result with { Sequence = 5, Deliver = true });
        Expect(!ledger.TryPrepareCompaction(player, 5, out _), "pending delivery must block retirement");
        Expect(ledger.TryPrepareCompaction(player, 3, out var compact), "completed result should be compactable");
        ledger = new TransactionLedger(compact.Contents);
        Expect(ledger.Lookup(completed, 3, out _) == TransactionLedger.LookupStatus.Retired, "retired request must not execute again");
        Expect(ledger.Lookup(pending, 5, out _) == TransactionLedger.LookupStatus.Retained, "pending result must remain recoverable");
        Expect(ledger.Lookup(guidOnly, null, out var retained) == TransactionLedger.LookupStatus.Retained &&
            retained.Matches(result.Fingerprint), "GUID-only request must retain its result and fingerprint");
    }

    private static void NetworkRecovery_DistinguishesCommittedResultsFromLookupFailures()
    {
        var lookup = NetworkSystem.Op.Lookup;
        var completed = NetworkSystem.CompletedOperationFlag;
        Expect(NetworkSystem.GetRecoveryAction(lookup, CommandStatus.NotFound, completed) == NetworkSystem.RecoveryAction.Retire,
            "a committed command's NotFound result still needs retirement");
        Expect(NetworkSystem.GetRecoveryAction(lookup, CommandStatus.InvalidArg, completed) == NetworkSystem.RecoveryAction.Retire,
            "a committed command's InvalidArg result must not pin the recovery journal");
        Expect(NetworkSystem.GetRecoveryAction(NetworkSystem.Op.Mutate, CommandStatus.Ok, completed) == NetworkSystem.RecoveryAction.Retire,
            "a durable final response can retire immediately");
        Expect(NetworkSystem.GetRecoveryAction(lookup, CommandStatus.NotFound, 0) == NetworkSystem.RecoveryAction.ForgetMissing,
            "a missing operation can be removed from the client journal");
        Expect(NetworkSystem.GetRecoveryAction(lookup, CommandStatus.PageBusy, 0) == NetworkSystem.RecoveryAction.Retry,
            "a transient store error must retain the operation");
        Expect(NetworkSystem.GetRecoveryAction(lookup, CommandStatus.Ok, NetworkSystem.GrantFlag) == NetworkSystem.RecoveryAction.Retry,
            "a delivered grant must wait for the server to settle the receipt");
        Expect(NetworkSystem.GetRecoveryAction(NetworkSystem.Op.Retire, CommandStatus.Retired, 0) == NetworkSystem.RecoveryAction.ForgetRetired,
            "server retirement confirmation permits local receipt cleanup");
    }

    private static void NetworkDelivery_SerializesRecoveryItemsOnlyForServer()
    {
        var state = new ServerDelivery { Planned = true, OwnerLineage = Guid.NewGuid() };
        state.AdvanceGeneration();
        state.RecoveryItems.Add(new Terraria.Item { type = 1, stack = 2 });
        using var diskStream = new MemoryStream(state.Write());
        var disk = Terraria.ModLoader.IO.TagIO.FromStream(diskStream, false);
        var wire = ServerDelivery.Read(state.Write(forClient: true));
        Expect(disk.GetLong("generation") == 1 && wire.Generation == 1 && wire.OwnerLineage == state.OwnerLineage,
            "generation and lineage must survive serialization");
        Expect(disk.ContainsKey("recoveryItems") && wire.RecoveryItems.Count == 0, "only the server needs recovery items");
    }

    private static void NetworkDelivery_RejectsGenerationZeroGrants()
    {
        var lineage = Guid.NewGuid();
        var delivery = new ServerDelivery { Planned = true, OwnerLineage = lineage };
        delivery.Grants.Add(new ServerDelivery.Grant(0, Array.Empty<byte>(), new Terraria.Item { type = 1, stack = 1 }));
        var rejected = false;
        try { _ = delivery.Write(forClient: true); }
        catch (InvalidDataException) { rejected = true; }
        Expect(rejected, "generation-zero data must never be offered as a grant");

        delivery.AdvanceGeneration();
        Expect(delivery.MatchesGrant(lineage, 1), "the owning save can settle its current grant");
        Expect(!delivery.MatchesGrant(Guid.NewGuid(), 1), "a different save lineage cannot settle a grant");
        Expect(!delivery.MatchesGrant(lineage, 0), "old receipts cannot settle the grant");
        delivery.AdvanceGeneration();
        Expect(!delivery.MatchesGrant(lineage, 1), "stale receipts cannot settle a replacement grant");
    }

    private static void NetworkReceipt_RequiresOwnerConnectionAndCompletedTransfer()
    {
        var owner = Guid.NewGuid();
        var otherConnectionForSamePlayer = Guid.NewGuid();
        var reply = Guid.NewGuid();
        Expect(NetworkSystem.CanAcknowledge(owner, owner, reply, reply), "owner may acknowledge completed reply");
        Expect(!NetworkSystem.CanAcknowledge(otherConnectionForSamePlayer, owner, reply, reply),
            "the second connection sharing the player's UUID must be rejected");
        Expect(!NetworkSystem.CanAcknowledge(owner, owner, reply, Guid.Empty), "incomplete reply cannot be acknowledged");
        Expect(!NetworkSystem.CanAcknowledge(owner, owner, Guid.NewGuid(), reply), "receipt must match the completed correlation");
    }
}
