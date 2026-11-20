using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using LargerInventory.Backend.Storage;
using Terraria;
using Terraria.ModLoader;

namespace LargerInventory.Backend.Commands;

internal sealed partial class CommandExecutor
{
    [ThreadStatic]
    private static CommandExecutor _workerExecutor;

    private CommandResult RunOnWorker(SlotWork work)
    {
        var previous = _workerExecutor;
        _workerExecutor = this;
        try { return work.Run(); }
        finally { _workerExecutor = previous; }
    }

    private int TryExecuteConcurrentWave(List<Command> batch, int start, HashSet<int> pinnedPages,
        HashSet<int> touchedPages)
    {
        var limit = Math.Min(Math.Clamp(_store.Config.MaxConcurrentCommandsPerBatch, 1, SlotConflictWave.MaxCommands),
            batch.Count - start);
        if (limit < 2 || !CanSchedule(batch[start], out _) || !CanSchedule(batch[start + 1], out _)) return 0;

        var wave = new SlotConflictWave();
        while (wave.Count < limit)
        {
            var command = batch[start + wave.Count];
            if (!CanSchedule(command, out var readOnly) || !wave.TryAdd(command.Access.Slots, readOnly)) break;
        }
        if (wave.Count < 2) return 0;

        for (var i = 0; i < wave.Count; i++)
        {
            var command = batch[start + i];
            foreach (var slot in command.Access.Slots)
                if (!_store.PageCache.TryGetLoaded(PageOf(slot), out _)) return 0;
            if (!Validate(command, out _)) return 0;
        }

        Span<int> acquired = stackalloc int[SlotConflictWave.MaxCommands * SlotConflictWave.MaxSlotsPerCommand];
        var acquiredCount = 0;
        for (var i = 0; i < wave.Count; i++)
        {
            foreach (var slot in batch[start + i].Access.Slots)
            {
                var pageIndex = PageOf(slot);
                if (_batchPages.Contains(pageIndex)) continue;
                _pageWork++;
                if (!_store.PageLocks.TryEnterUse(pageIndex))
                {
                    foreach (var held in acquired[..acquiredCount])
                    {
                        _batchPages.Remove(held);
                        _store.PageLocks.ExitUse(held);
                    }
                    return 0;
                }
                _batchPages.Add(pageIndex);
                acquired[acquiredCount++] = pageIndex;
            }
        }

        var work = new SlotWork[wave.Count];
        for (var i = 0; i < work.Length; i++)
        {
            var command = batch[start + i];
            _reservedSlots.UnionWith(command.Access.Slots);
            try { work[i] = CaptureSlotWork(command, pinnedPages); }
            finally { _reservedSlots.Clear(); }
        }

        var tasks = new Task<CommandResult>[work.Length - 1];
        var scheduled = 0;
        CommandResult last = default;
        try
        {
            for (; scheduled < tasks.Length; scheduled++)
            {
                var entry = work[scheduled];
                tasks[scheduled] = Task.Run(() => RunOnWorker(entry));
            }
            // Use the owner for one command to save a thread-pool dispatch.
            last = work[^1].Run();
        }
        finally
        {
            for (var i = 0; i < scheduled; i++) tasks[i].GetAwaiter().GetResult();
        }

        for (var i = 0; i < work.Length; i++)
        {
            var result = i == tasks.Length ? last : tasks[i].Result;
            work[i].Publish(this, touchedPages);
            work[i].Command.Complete(result);
        }
        _store.SyncSummaries(touchedPages);
        touchedPages.Clear();
        return work.Length;
    }

    private static bool CanSchedule(Command command, out bool readOnly)
    {
        readOnly = command.Kind == CommandKind.QuerySlot || command.InputPayload is EscapePayload { ReadOnly: true };
        if (!command.Access.IsStrong || command.Access.Slots.Length is 0 or > SlotConflictWave.MaxSlotsPerCommand)
            return false;
        return command.Kind switch
        {
            CommandKind.QuerySlot or CommandKind.Take or CommandKind.Move => true,
            CommandKind.Put => command.InputPayload is PutPayload { Items.Count: <= SlotConflictWave.MaxSlotsPerCommand },
            CommandKind.Escape => command.InputPayload is EscapePayload { Concurrent: true },
            _ => false
        };
    }

    private SlotWork CaptureSlotWork(Command command, HashSet<int> pinnedPages)
    {
        var work = new SlotWork(command);
        for (var i = 0; i < work.Slots.Length; i++)
        {
            var slotId = command.Access.Slots[i];
            if (!TryGetPageForSlot(slotId, pinnedPages, out var page))
                throw new InvalidOperationException("A reserved wave page became unavailable.");
            work.Pages[i] = page;
            work.Slots[i] = page.Slots[SlotOf(slotId)];
        }
        return work;
    }

    private CommandResult ExecuteConcurrentEscape(Command command, HashSet<int> touchedPages, HashSet<int> pinnedPages)
    {
        // A solitary, oversized or cold opt-in command keeps the same snapshot semantics.
        var work = new SlotWork(command);
        for (var i = 0; i < work.Slots.Length; i++)
        {
            var slotId = command.Access.Slots[i];
            if (!TryGetPageForSlot(slotId, pinnedPages, out var page))
                return CommandResult.Fail(_pageBusyHit ? CommandStatus.PageBusy : CommandStatus.PageLoadFailed);
            work.Pages[i] = page;
            work.Slots[i] = page.Slots[SlotOf(slotId)];
        }
        var result = work.Run();
        work.Publish(this, touchedPages);
        return result;
    }

    private sealed class SlotWork(Command command)
    {
        public Command Command { get; } = command;
        public Item[] Slots { get; } = new Item[command.Access.Slots.Length];
        public ItemPage[] Pages { get; } = new ItemPage[command.Access.Slots.Length];
        private bool _publish;
        private uint _changed;
        private Item[] _escapeWrites;

        public CommandResult Run()
        {
            try
            {
                return Command.Kind switch
                {
                    CommandKind.Escape => RunEscape((EscapePayload)Command.InputPayload),
                    CommandKind.QuerySlot => RunQuery(),
                    CommandKind.Take => RunTake((TakePayload)Command.InputPayload),
                    CommandKind.Put => RunPut((PutPayload)Command.InputPayload),
                    CommandKind.Move => RunMove((MovePayload)Command.InputPayload),
                    _ => CommandResult.Fail(CommandStatus.InvalidArg)
                };
            }
            catch (Exception e)
            {
                Debug.Error_Internal(e);
                return Command.FailUnexecuted(Command, Command.Kind == CommandKind.Escape
                    ? CommandStatus.EscapeError | CommandStatus.InternalError : CommandStatus.InternalError);
            }
        }

        private CommandResult RunEscape(EscapePayload payload)
        {
            var copies = Array.ConvertAll(Slots, item => item.Clone());
            var result = payload.Callback(copies);
            if (!payload.ReadOnly && result.IsOk)
            {
                // Publish fresh copies so references captured by the extension cannot mutate the store.
                _escapeWrites = Array.ConvertAll(copies, item => item is null || item.IsAir ? new Item() : item.Clone());
                _publish = true;
            }
            return result;
        }

        private CommandResult RunQuery()
        {
            var results = new SlotInfo[Slots.Length];
            for (var i = 0; i < Slots.Length; i++)
            {
                var item = Slots[i];
                results[i] = new SlotInfo { SlotId = Command.Access.Slots[i], Type = item.type,
                    Stack = item.stack, Favorited = item.favorited, IsAir = item.IsAir };
            }
            return CommandResult.Ok(results);
        }

        private CommandResult RunTake(TakePayload payload)
        {
            var results = new List<Item>();
            var remaining = payload.Count;
            var all = remaining == int.MaxValue;
            var status = CommandStatus.Ok;
            try
            {
                for (var i = 0; i < Slots.Length && remaining > 0; i++)
                {
                    var item = Slots[i];
                    if (item.IsAir || (payload.AvoidFavorited && item.favorited)) continue;
                    if (item.type != payload.Type || (payload.DestinationSnapshot is not null &&
                        !ItemLoader.CanStack(payload.DestinationSnapshot.Clone(), item.Clone())))
                    {
                        status |= CommandStatus.TypeMismatch;
                        continue;
                    }
                    var count = all ? item.stack : Math.Min(item.stack, remaining);
                    if (count == item.stack)
                    {
                        Set(i, new Item());
                        results.Add(item);
                    }
                    else
                    {
                        var rest = item.Clone();
                        results.Add(Split(rest, count));
                        Set(i, rest);
                    }
                    if (!all) remaining -= count;
                }
            }
            catch (Exception e)
            {
                Debug.Error_Internal(e);
                status |= CommandStatus.InternalError;
            }
            if ((!all && remaining != 0) || results.Count == 0) status |= CommandStatus.NotEnough;
            if (results.Count > 0 && status != CommandStatus.Ok) status |= CommandStatus.Partial;
            _publish = true;
            return new CommandResult(status, results);
        }

        private CommandResult RunPut(PutPayload payload)
        {
            foreach (var input in payload.Items)
            {
                var source = input;
                var blocked = CommandStatus.Ok;
                for (var i = 0; i < Slots.Length && !source.IsAir; i++)
                {
                    var item = Slots[i];
                    if (item.IsAir) continue;
                    if (source.type != item.type)
                        blocked |= CommandStatus.TargetOccupied | CommandStatus.TypeMismatch;
                    else if (item.stack >= item.maxStack)
                        blocked |= CommandStatus.TargetOccupied;
                    else if (TryMerge(item, source, out var next, out var remainder, out _))
                    {
                        Set(i, next);
                        source = remainder;
                    }
                    else blocked |= CommandStatus.TargetOccupied | CommandStatus.TypeMismatch;
                }
                for (var i = 0; i < Slots.Length && source is not null && !source.IsAir; i++)
                {
                    if (!Slots[i].IsAir) continue;
                    if (source.stack <= source.maxStack)
                    {
                        Set(i, source);
                        source = null;
                    }
                    else
                    {
                        var remainder = source.Clone();
                        Set(i, Split(remainder, source.maxStack));
                        source = remainder;
                    }
                }
                if (source is not null && !source.IsAir)
                    return Command.FailUnexecuted(Command, CommandStatus.NoSpace | blocked);
            }
            _publish = true;
            return CommandResult.Ok(new List<Item>());
        }

        private CommandResult RunMove(MovePayload payload)
        {
            var from = Command.Access.Slots[0] == payload.SourceSlot ? 0 : 1;
            var to = 1 - from;
            var source = Slots[from];
            var destination = Slots[to];
            if (source.IsAir) return CommandResult.Fail(CommandStatus.NotFound);
            if (destination.IsAir)
            {
                Set(to, source);
                Set(from, new Item());
                _publish = true;
                return CommandResult.Ok(source.stack);
            }
            if (destination.type != source.type)
                return CommandResult.Fail(CommandStatus.TargetOccupied | CommandStatus.TypeMismatch);
            if (destination.stack >= destination.maxStack)
                return CommandResult.Fail(CommandStatus.TargetOccupied);
            if (!TryMerge(destination, source, out var next, out var remainder, out var moved))
                return CommandResult.Fail(CommandStatus.TargetOccupied | CommandStatus.TypeMismatch);
            Set(to, next);
            Set(from, remainder.IsAir ? new Item() : remainder);
            _publish = true;
            return new CommandResult(remainder.IsAir ? CommandStatus.Ok : CommandStatus.Partial | CommandStatus.TargetOccupied, moved);
        }

        private void Set(int index, Item item)
        {
            Slots[index] = item;
            _changed |= 1u << index;
        }

        public void Publish(CommandExecutor executor, HashSet<int> touchedPages)
        {
            if (!_publish) return;
            for (var i = 0; i < Slots.Length; i++)
            {
                if (_escapeWrites is null && (_changed & (1u << i)) == 0) continue;
                var page = Pages[i];
                page.Slots[SlotOf(Command.Access.Slots[i])] = (_escapeWrites ?? Slots)[i];
                executor.MarkChanged(page, touchedPages);
            }
        }
    }
}
