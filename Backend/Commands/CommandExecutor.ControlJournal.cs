using System;
using System.Collections.Generic;
using LargerInventory.Backend.Storage;
using Terraria;

namespace LargerInventory.Backend.Commands;

internal sealed partial class CommandExecutor
{
    private sealed class ControlJournal(HashSet<int> touchedPages, HashSet<int> pinnedPages)
    {
        public readonly HashSet<int> TouchedPages = touchedPages;
        public readonly HashSet<int> PinnedPages = pinnedPages;
        public readonly List<ControlUndo> Entries = new();
        public readonly Dictionary<ItemPage, int> NewPages = new();
    }

    private readonly record struct ControlUndo(ItemPage Page, int Slot, Item Original);

    private const int NewPageUndo = -1;
    private static readonly Dictionary<int, TypeStat> EmptyControlPageTypes = new();
    private ControlJournal _controlJournal;

    private void BeginControlJournal(HashSet<int> touchedPages, HashSet<int> pinnedPages)
    {
        if (_controlJournal is not null)
            throw new InvalidOperationException("The control journal is already active.");
        _controlJournal = new ControlJournal(touchedPages, pinnedPages);
    }

    private void EndControlJournal() => _controlJournal = null;

    private int ControlJournalCheckpoint() => ActiveControlJournal().Entries.Count;

    private ControlJournal ActiveControlJournal() => _controlJournal ??
        throw new InvalidOperationException("The control journal is not active.");

    private void BeforeSlotWrite(ItemPage page, int slot)
    {
        if (_controlJournal is not { } journal) return;

        // Journaled commands replace slot references. Escape publishes detached items
        // so the original remains safe to restore without another clone.
        journal.Entries.Add(new ControlUndo(page, slot, page.Slots[slot]));
    }

    private void RecordNewControlPage(ItemPage page)
    {
        if (_controlJournal is not { } journal) return;

        journal.NewPages.Add(page, journal.Entries.Count);
        journal.Entries.Add(new ControlUndo(page, NewPageUndo, null));
    }

    private void ForgetNewControlPage(ItemPage page)
    {
        if (_controlJournal is not { } journal || !journal.NewPages.Remove(page, out var index)) return;

        // Put owns this removal. Preserve checkpoint positions and skip stale writes to
        // this instance during restore, even if another allocation reuses its index.
        journal.Entries[index] = default;
    }

    private void RestoreControlJournal(int position)
    {
        var journal = ActiveControlJournal();
        var entries = journal.Entries;
        if ((uint)position > (uint)entries.Count)
            throw new ArgumentOutOfRangeException(nameof(position));

        for (var i = entries.Count - 1; i >= position; i--)
        {
            var entry = entries[i];
            var page = entry.Page;
            if (page is null || !_store.PageCache.Directory.TryGet(page.PageIndex, out var current) ||
                !ReferenceEquals(page, current))
                continue;

            if (entry.Slot == NewPageUndo)
            {
                RemoveNewControlPage(page, journal);
            }
            else
            {
                page.Slots[entry.Slot] = entry.Original;
                // A predicate or weak command may already have published tentative summaries.
                MarkChanged(page, journal.TouchedPages);
            }
        }

        entries.RemoveRange(position, entries.Count - position);
    }

    private void RemoveNewControlPage(ItemPage page, ControlJournal journal)
    {
        var pageIndex = page.PageIndex;
        // The index reflects the last published summary, which may differ from the current slots.
        if (_store.Config.EnableGlobalAggregate && page.Indexed)
            _store.TypeIndex.UpdatePage(pageIndex, page.Summary.Types, EmptyControlPageTypes);
        page.Indexed = false;
        _store.PageCache.RemoveNewPage(page);
        if (journal.PinnedPages.Remove(pageIndex) && page.PinCount > 0)
            page.PinCount--;
        journal.NewPages.Remove(page);
        journal.TouchedPages.Remove(pageIndex);
        if (_batchPages.Remove(pageIndex))
            _store.PageLocks.ExitUse(pageIndex);
    }
}
