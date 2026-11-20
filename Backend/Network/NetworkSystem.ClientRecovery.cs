using System;
using System.Linq;
using LargerInventory.Backend.Commands;
using LargerInventory.Backend.Integration;
using LargerInventory.Backend.Storage;
using Terraria;

namespace LargerInventory.Backend.Network;

public static partial class NetworkSystem
{
    private static ClientOperationJournal _clientJournal;
    private static long _playerHigh, _worldHigh, _nextRecovery;

    private static StoreScope JournalScope(Scope scope) => scope == Scope.World ? StoreScope.World : StoreScope.Player;

    private static void OpenClientRecovery(Session session, long playerHigh, long worldHigh)
    {
        _clientJournal = null;
        _playerHigh = playerHigh;
        _worldHigh = worldHigh;
        _nextRecovery = 0;
        var file = Main.ActivePlayerFileData;
        if (file is not null && ClientOperationJournal.TryOpen(Main.SavePath, session.Player, session.World,
                file.Path, file.IsCloudSave, out var journal))
        {
            _clientJournal = journal;
            session.JournalLineage = journal.Lineage;
        }
    }

    private static bool StageRecoverableRequest(Scope scope, Guid request, out long sequence)
    {
        sequence = 0;
        if (_clientJournal is null || _clientJournal.GetPending().Any(entry => entry.Scope == JournalScope(scope)))
            return false;
        if (!_clientJournal.TryStage(JournalScope(scope), request, scope == Scope.World ? _worldHigh : _playerHigh, out var entry))
            return false;
        sequence = entry.Sequence;
        return true;
    }

    private static void PumpClientRecovery(long now)
    {
        var config = RuntimeConfig;
        if (_clientJournal is null || now < _nextRecovery || Requests.Count >= config.MaxPendingNetworkRequests)
            return;
        _nextRecovery = now + config.NetworkRecoveryRetryMs;
        foreach (var entry in _clientJournal.GetPending())
        {
            if (Requests.Values.Any(pending => pending.Request == entry.OperationId))
                continue;
            var scope = entry.Scope == StoreScope.World ? Scope.World : Scope.Player;
            StartRequest(scope, Op.Lookup, entry.OperationId, null, sequence: entry.Sequence);
        }
    }

    private static void AdvanceClientRecovery(Pending pending, CommandResult result, byte flags)
    {
        if (pending.Sequence <= 0 || _clientJournal is null || !_clientJournal.TryGet(pending.Request, out var entry) ||
            entry.Sequence != pending.Sequence)
            return;
        switch (GetRecoveryAction(pending.Operation, result.Status, flags))
        {
            case RecoveryAction.ForgetRetired:
                InventoryClientState.ForgetRetiredReceipt(Main.LocalPlayer, ReceiptKey(_client, pending.Scope, pending.Request));
                _clientJournal.TryConfirmRetired(entry);
                break;
            case RecoveryAction.ForgetMissing:
                _clientJournal.TryConfirmRetired(entry);
                break;
            case RecoveryAction.Retire:
                StartRequest(pending.Scope, Op.Retire, pending.Request, null, sequence: pending.Sequence);
                break;
        }
    }

    internal enum RecoveryAction { Retry, Retire, ForgetMissing, ForgetRetired }

    internal static RecoveryAction GetRecoveryAction(Op operation, CommandStatus status, byte flags)
    {
        if (flags == CompletedOperationFlag)
            return status != CommandStatus.InternalError && operation is Op.Lookup or Op.Mutate or Op.Put or Op.DebugFill
                ? RecoveryAction.Retire : RecoveryAction.Retry;
        if (flags == 0 && status == CommandStatus.Retired)
            return RecoveryAction.ForgetRetired;
        if (flags == 0 && operation == Op.Lookup && status == CommandStatus.NotFound)
            return RecoveryAction.ForgetMissing;
        return RecoveryAction.Retry;
    }
}
