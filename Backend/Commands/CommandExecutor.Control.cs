using System;
using System.Collections.Generic;
using System.Linq;
using LargerInventory.Backend.Storage;
using Terraria;

namespace LargerInventory.Backend.Commands;

internal sealed partial class CommandExecutor
{
    private void ExecuteControlBatch(IReadOnlyList<Command> commands,
        Func<BranchContext, ControlTransactionDecision> settle = null, Command envelope = null)
    {
        var labels = new Dictionary<BranchLabel, int>();
        var results = new Dictionary<Command, CommandResult>();
        Dictionary<Command, List<Item>> outputs = null;
        var savepoints = new List<(ControlSavepoint Handle, int Position,
            Dictionary<Command, CommandResult> Results, int Reservations)>();
        var touched = new HashSet<int>();
        var pinned = new HashSet<int>();
        var status = CommandStatus.Ok;
        var transactional = settle is not null;
        var rolledBack = false;
        var confirmationFailed = false;
        var hasTake = commands.Any(command => command.Kind == CommandKind.Take);
        if (hasTake) BeginTakeBatch(touched, pinned);
        else if (transactional) BeginControlJournal(touched, pinned);

        ControlSavepoint Save()
        {
            var handle = new ControlSavepoint();
            savepoints.Add((handle, ControlJournalCheckpoint(), new(results),
                _takeReservations?.Count ?? 0));
            return handle;
        }

        void Restore(ControlSavepoint handle)
        {
            var index = savepoints.FindIndex(entry => ReferenceEquals(entry.Handle, handle));
            if (index < 0)
                throw new InvalidOperationException("The savepoint is no longer valid.");
            var snapshot = savepoints[index];
            RestoreControlJournal(snapshot.Position);
            results.Clear();
            foreach (var pair in snapshot.Results)
                results.Add(pair.Key, pair.Value);
            if (hasTake) RestoreReservations(snapshot.Reservations);
            savepoints.RemoveRange(index + 1, savepoints.Count - index - 1);
        }

        try
        {
            for (var i = 0; i < commands.Count; i++)
                if (commands[i].Kind == CommandKind.Label)
                    labels.Add((BranchLabel)commands[i].InputPayload, i);

            var pc = 0;
            var steps = 0;
            while (pc < commands.Count)
            {
                if (Integration.InventorySystem.CommandsPaused)
                {
                    status = CommandStatus.ManualInterrupt;
                    break;
                }
                if (steps++ >= Math.Max(1, _store.Config.MaxControlFlowSteps))
                {
                    status = CommandStatus.ControlFlowLimit;
                    break;
                }

                var command = commands[pc++];
                try
                {
                    switch (command.Kind)
                    {
                        case CommandKind.Label:
                            results[command] = CommandResult.Ok();
                            break;
                        case CommandKind.Br:
                        {
                            var branch = (BranchPayload)command.InputPayload;
                            var ctx = new BranchContext(results);
                            bool jump;
                            try { jump = branch.Predicate(ctx); }
                            finally { ctx.Close(); }
                            results[command] = CommandResult.Ok(jump);
                            if (jump)
                                pc = labels[branch.Target];
                            break;
                        }
                        case CommandKind.TransactionAction:
                        {
                            var ctx = new BranchContext(results, Save, Restore);
                            try { ((Action<BranchContext>)command.InputPayload)(ctx); }
                            finally { ctx.Close(); }
                            results[command] = CommandResult.Ok();
                            break;
                        }
                        default:
                        {
                            // A successful Put consumed its input ownership. Revisiting it has no pending input.
                            if (command.Kind == CommandKind.Put && results.TryGetValue(command, out var previous) &&
                                previous.ResultPayload is ControlPutResult { Returned.Count: 0 } put && previous.IsOk)
                            {
                                results[command] = CommandResult.Ok(new ControlPutResult(default, put.Returned));
                                break;
                            }
                            var putItems = (command.InputPayload as PutPayload)?.Items;
                            var requested = putItems?.Sum(item => (long)(item?.stack ?? 0)) ?? 0;
                            var result = ExecuteControlInstruction(command, transactional || hasTake, touched, pinned);
                            if (command.Kind == CommandKind.Put)
                            {
                                var returned = result.ResultPayload as List<Item> ?? new List<Item>();
                                var preview = result.IsOk ? new PutPreview(requested, 0) : new PutPreview(0, requested);
                                result = new CommandResult(result.Status, new ControlPutResult(preview, returned));
                            }
                            results[command] = result;
                            break;
                        }
                    }
                }
                catch (Exception error)
                {
                    Debug.Error_Internal(error);
                    status = CommandStatus.InternalError;
                    results[command] = CommandResult.Fail(status);
                    break;
                }
            }

            if (transactional)
            {
                var decision = ControlTransactionDecision.Rollback;
                var ctx = new BranchContext(results, executionStatus: status);
                try { decision = settle(ctx); }
                catch (Exception error)
                {
                    Debug.Error_Internal(error);
                    status = CommandStatus.InternalError;
                }
                finally { ctx.Close(); }
                if (decision != ControlTransactionDecision.Commit)
                {
                    RestoreControlJournal(0);
                    rolledBack = true;
                }
            }
            if (hasTake && !rolledBack)
            {
                try { outputs = ConfirmReservations(touched); }
                catch (Exception error)
                {
                    Debug.Error_Internal(error);
                    status |= CommandStatus.InternalError;
                    confirmationFailed = true;
                    RestoreControlJournal(0);
                    rolledBack = true;
                }
            }
        }
        finally
        {
            if (hasTake) EndTakeBatch();
            else if (transactional) EndControlJournal();
            FinishBatch(touched);
        }

        foreach (var command in commands)
        {
            if (rolledBack)
            {
                var rollbackStatus = CommandStatus.RolledBack;
                if (confirmationFailed) rollbackStatus |= CommandStatus.InternalError;
                command.Complete(Command.FailUnexecuted(command, rollbackStatus));
                continue;
            }
            if (!results.TryGetValue(command, out var result))
                result = Command.FailUnexecuted(command, status == CommandStatus.Ok ? CommandStatus.Skipped : status);
            else if (command.Kind == CommandKind.Take)
                result = new CommandResult(result.Status, outputs?.GetValueOrDefault(command) ?? new List<Item>());
            else if (result.ResultPayload is ControlPutResult put)
                result = new CommandResult(result.Status, put.Returned);
            command.Complete(result);
        }
        if (envelope is not null)
            envelope.Complete(new CommandResult(rolledBack ? status | CommandStatus.RolledBack : status));
        foreach (var command in commands)
            NotifyCallback(command);
        if (envelope is not null)
            NotifyCallback(envelope);
    }

    private CommandResult ExecuteControlInstruction(Command command, bool transactional,
        HashSet<int> touched, HashSet<int> pinned)
    {
        if (!Validate(command, out var failure))
            return failure;
        _pageBusyHit = false;
        _pageLoadFailedHit = false;
        try
        {
            if (command.Access.IsStrong && !TryReserveStrongSlots(command.Access))
                return Command.FailUnexecuted(command, CommandStatus.PageBusy);
            if (touched.Count != 0 && !command.Access.IsStrong && command.Kind != CommandKind.NopBoundary)
            {
                _store.SyncSummaries(touched);
                touched.Clear();
            }
            return RunCommandSafe(command, touched, pinned, transactional);
        }
        finally { _reservedSlots.Clear(); }
    }
}
