using System;
using System.Collections.Generic;
using LargerInventory.Backend.Storage;
using Terraria;

namespace LargerInventory.Backend.Commands;

internal sealed partial class CommandExecutor
{
    private readonly record struct TakeReservation(Command Command, ItemPage Page, int Slot, Item Original, int Count);
    private List<TakeReservation> _takeReservations;
    private Dictionary<int, int> _reservedQuantities;
    private Dictionary<(int PageIndex, int Type), (long Count, long Favorites)> _reservedPageTypes;
    private Dictionary<Command, CommandResult> _takeBatchResults;

    private void BeginTakeBatch(HashSet<int> touched, HashSet<int> pinned)
    {
        BeginControlJournal(touched, pinned);
        _takeReservations = new();
        _reservedQuantities = new();
        _reservedPageTypes = new();
        _takeBatchResults = new();
    }

    private void EndTakeBatch()
    {
        EndControlJournal();
        _takeReservations = null;
        _reservedQuantities = null;
        _reservedPageTypes = null;
        _takeBatchResults = null;
    }

    private void CompleteBatchInstruction(Command command, CommandResult result)
    {
        if (_takeBatchResults is null) command.Complete(result);
        else _takeBatchResults.Add(command, result);
    }

    private int ReservedQuantity(int slotId) => _reservedQuantities?.GetValueOrDefault(slotId) ?? 0;

    private void AddTakeReservation(Command command, ItemPage page, int slot, Item item, int count)
    {
        var slotId = (page.PageIndex << 10) | slot;
        _takeReservations.Add(new TakeReservation(command, page, slot, item, count));
        _reservedQuantities[slotId] = ReservedQuantity(slotId) + count;
        UpdateReservedPageType(page, slot, item, count);
    }

    private void RestoreReservations(int checkpoint)
    {
        for (var i = _takeReservations.Count - 1; i >= checkpoint; i--)
        {
            var reservation = _takeReservations[i];
            var slotId = (reservation.Page.PageIndex << 10) | reservation.Slot;
            var remaining = ReservedQuantity(slotId) - reservation.Count;
            if (remaining == 0) _reservedQuantities.Remove(slotId);
            else _reservedQuantities[slotId] = remaining;
            UpdateReservedPageType(reservation.Page, reservation.Slot, reservation.Original, -reservation.Count);
        }
        _takeReservations.RemoveRange(checkpoint, _takeReservations.Count - checkpoint);
    }

    private void UpdateReservedPageType(ItemPage page, int slot, Item item, int delta)
    {
        var key = (page.PageIndex, item.type);
        var totals = _reservedPageTypes.GetValueOrDefault(key);
        totals.Count += delta;
        // Page summaries count favorite stacks, rather than the items in them.
        if (item.favorited)
        {
            var previous = ReservedQuantity((page.PageIndex << 10) | slot) - delta;
            if (previous == item.stack) totals.Favorites--;
            if (previous + delta == item.stack) totals.Favorites++;
        }
        if (totals.Count == 0) _reservedPageTypes.Remove(key);
        else _reservedPageTypes[key] = totals;
    }

    private Dictionary<Command, List<Item>> ConfirmReservations(HashSet<int> touched)
    {
        var deliveries = new Dictionary<Command, List<Item>>();
        var remainders = new Dictionary<int, (ItemPage Page, int Slot, Item Item)>();
        foreach (var reservation in _takeReservations)
        {
            var (command, page, slot, original, count) = reservation;
            var slotId = (page.PageIndex << 10) | slot;
            if (!ReferenceEquals(original, page.Slots[slot]))
                throw new InvalidOperationException("A reserved slot changed before confirmation.");
            var staged = remainders.TryGetValue(slotId, out var previous);
            var source = staged ? previous.Item : original;
            if (source.IsAir || source.stack < count)
                throw new InvalidOperationException("A reserved stack no longer has the requested quantity.");

            Item taken;
            if (source.stack == count)
            {
                taken = source;
                source = new Item();
            }
            else
            {
                if (!staged) source = source.Clone();
                taken = Split(source, count);
            }
            remainders[slotId] = (page, slot, source);
            if (!deliveries.TryGetValue(command, out var items))
                deliveries.Add(command, items = new List<Item>());
            items.Add(taken);
        }

        // Stage all splits before replacing any physical slot.
        foreach (var (page, slot, item) in remainders.Values)
        {
            BeforeSlotWrite(page, slot);
            page.Slots[slot] = item;
            MarkChanged(page, touched);
        }
        return deliveries;
    }
}
