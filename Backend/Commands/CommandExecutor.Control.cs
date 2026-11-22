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
        var outputs = new Dictionary<Command, List<Item>>();
        var savepoints = new List<(ControlSavepoint Handle, int Position,
            Dictionary<Command, CommandResult> Results, Dictionary<Command, int> Counts)>();
        var touched = new HashSet<int>();
        var pinned = new HashSet<int>();
        var status = CommandStatus.Ok;
        var transactional = settle is not null;
        var rolledBack = false;
        if (transactional)
            BeginControlJournal(touched, pinned);

        ControlSavepoint Save()
        {
            var handle = new ControlSavepoint();
            savepoints.Add((handle, ControlJournalCheckpoint(), new(results),
                outputs.ToDictionary(pair => pair.Key, pair => pair.Value.Count)));
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
            foreach (var command in outputs.Keys.ToArray())
            {
                if (!snapshot.Counts.TryGetValue(command, out var count))
                    outputs.Remove(command);
                else
                    outputs[command].RemoveRange(count, outputs[command].Count - count);
            }
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
                                previous.ResultPayload is List<Item> { Count: 0 })
                            {
                                results[command] = CommandResult.Ok(previous.ResultPayload);
                                break;
                            }
                            var result = ExecuteControlInstruction(command, transactional, touched, pinned);
                            if (command.Kind == CommandKind.Take && result.ResultPayload is List<Item> items)
                            {
                                if (!outputs.TryGetValue(command, out var delivery))
                                    outputs.Add(command, delivery = new List<Item>());
                                delivery.AddRange(items);
                                result = new CommandResult(result.Status, items.Sum(item => (long)item.stack));
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
        }
        finally
        {
            if (transactional)
                EndControlJournal();
            FinishBatch(touched);
        }

        foreach (var command in commands)
        {
            if (rolledBack)
            {
                command.Complete(Command.FailUnexecuted(command, CommandStatus.RolledBack));
                continue;
            }
            if (!results.TryGetValue(command, out var result))
                result = Command.FailUnexecuted(command, status == CommandStatus.Ok ? CommandStatus.Skipped : status);
            else if (outputs.TryGetValue(command, out var delivery))
                result = new CommandResult(result.Status, delivery);
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
