using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using LargerInventory.Backend.Storage;

namespace LargerInventory.Backend.Repair;

/// <summary>Runs bounded repair work under the store execution gate. ItemIO stays on the owner thread.</summary>
internal sealed class PageRepairService : IDisposable
{
    private readonly InventoryStore _store;
    private readonly FilePageFileStore _files;
    private RepairReport _report = new();
    private Queue<(int Index, int Attempts)> _pending = new();
    private readonly List<int> _repaired = new();
    private readonly List<int> _lockFailed = new();
    private Action<int> _pageChanged;
    private Action<RepairReport> _completed;
    private string _archiveDirectory;
    private volatile bool _cancel;
    private volatile bool _running;

    public PageRepairService(InventoryStore store)
    {
        _store = store;
        _files = store.FileStore as FilePageFileStore;
    }

    public bool IsRunning => _running;
    public RepairReport Snapshot() => _report.Copy();

    public bool Start(Action<int> pageChanged, Action<RepairReport> completed, out string error)
    {
        error = null;
        if (_running || _files is null)
        {
            error = _running ? "repair already running" : "store has no persistent page files";
            return false;
        }
        var indices = new SortedSet<int>(_files.ListPages());
        foreach (var page in _store.PageCache.AllocatedPages)
            indices.Add(page.PageIndex);
        _pending.Clear();
        foreach (var index in indices)
        {
            _store.PageCache.Directory.GetOrCreate(index);
            _pending.Enqueue((index, 0));
        }
        _report = new RepairReport { TotalPages = indices.Count };
        _archiveDirectory = Path.Combine(_store.DirectoryPath, "repair-archive",
            DateTime.UtcNow.ToString("yyyyMMdd-HHmmss.fffffff'Z'", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N"));
        _repaired.Clear();
        _lockFailed.Clear();
        _pageChanged = pageChanged;
        _completed = completed;
        _cancel = false;
        _running = true;
        return true;
    }

    public void Pump()
    {
        if (!_running)
            return;
        var config = _store.Config;
        // A page requeued after contention should wait until the next pump.
        var budget = Math.Min(config.MaxRepairPagesPerPump, _pending.Count);
        for (var attempt = 0; attempt < budget && !_cancel && _pending.TryDequeue(out var next); attempt++)
        {
            if (!_store.PageLocks.TryEnter(next.Index))
            {
                if (next.Attempts < config.MaxRepairPageLockRetries)
                    _pending.Enqueue((next.Index, next.Attempts + 1));
                else
                {
                    _lockFailed.Add(next.Index);
                    _report.LockFailedPages++;
                }
                continue;
            }
            var changed = false;
            try
            {
                _report.ScannedPages++;
                changed = Repair(next.Index);
            }
            catch (Exception e)
            {
                _report.FailedPages++;
                if (e is IOException or UnauthorizedAccessException)
                    _report.IoErrorPages++;
                Log.Error($"Could not repair page {next.Index}.", e);
            }
            finally { _store.PageLocks.Exit(next.Index); }

            // The callback can load the repaired page. Pump still holds the store execution gate.
            if (changed)
                Notify(() => _pageChanged?.Invoke(next.Index));
        }
        if (_cancel || _pending.Count == 0)
            Finish();
    }

    private bool Repair(int index)
    {
        _store.PageCache.Directory.TryGet(index, out var page);
        if (page.Dirty)
        {
            _report.SkippedInUsePages++;
            return false;
        }
        var primary = _files.ReadCopy(index, backup: false, materialize: true);
        if (primary.Success)
        {
            _report.HealthyPages++;
            return false;
        }
        var backup = _files.ReadCopy(index, backup: true, materialize: true);
        if (ReportPreservedFailure(index, primary, backup))
            return false;
        // A loaded clean page may hold newer valid data than a backup. Never replace it behind the cache.
        if (page.Loaded && !(backup.Success && _store.PageCache.WasLoadedFromBackup(index)))
        {
            _report.SkippedInUsePages++;
            return false;
        }
        var summary = new PageSummary { Version = unchecked(page.Summary.Version + 1) };
        if (backup.Success)
        {
            summary.RebuildFrom(backup.Items);
            _store.CommitRepairPage(index, FilePageFileStore.UpgradeCopy(index, backup, summary), summary, deleteBackup: false);
            _report.RecoverablePages++;
            return true;
        }
        if (primary.NotFound && backup.NotFound && !page.KnownNonEmpty)
        {
            _report.HealthyPages++;
            return false;
        }
        // Copy exact raw bytes to the same attempt directory before publishing the replacement.
        // Failures here leave both live files untouched. A failed commit can leave a useful extra archive.
        if (primary.Bytes is not null || backup.Bytes is not null)
        {
            _report.ArchiveDirectory ??= _archiveDirectory;
            Directory.CreateDirectory(_report.ArchiveDirectory);
            var name = Path.GetFileName(FilePageFileStore.FileName(index));
            if (primary.Bytes is not null)
                StoreRedoJournal.WriteNew(Path.Combine(_report.ArchiveDirectory, name), primary.Bytes);
            if (backup.Bytes is not null)
                StoreRedoJournal.WriteNew(Path.Combine(_report.ArchiveDirectory, name + ".bak"), backup.Bytes);
        }
        _store.CommitRepairPage(index, FilePageFileStore.Snapshot(index, null, summary), summary, deleteBackup: backup.Bytes is not null);
        _repaired.Add(index);
        _report.RepairedPages++;
        return true;
    }

    private bool ReportPreservedFailure(int index, PageLoadResult primary, PageLoadResult backup)
    {
        bool Has(PageFileState state) => primary.State == state || backup.State == state;
        var io = Has(PageFileState.IoError);
        var version = Has(PageFileState.UnsupportedVersion);
        var mod = Has(PageFileState.ModError);
        if (!io && !version && !mod)
            return false;
        if (io) _report.IoErrorPages++;
        if (version) _report.UnsupportedVersionPages++;
        if (mod) _report.ModErrorPages++;
        _report.FailedPages++;
        Log.Warn($"Repair preserved page {index}: primary {primary.State} ({primary.Error}); backup {backup.State} ({backup.Error}).");
        return true;
    }

    private void Finish()
    {
        _report.Canceled = _cancel;
        _report.RepairedPageIndices = _repaired.ToArray();
        _report.LockFailedPageIndices = _lockFailed.ToArray();
        foreach (var page in _store.PageCache.AllocatedPages)
            if (page.LoadFailed)
                _report.RemainingFailedPages++;
        _pending.Clear();
        _running = false;
        var completed = _completed;
        _completed = null;
        _pageChanged = null;
        Notify(() => completed?.Invoke(Snapshot()));
    }

    private static void Notify(Action callback)
    {
        try { callback(); }
        catch (Exception e) { Log.Error("Page repair callback failed.", e); }
    }

    public void Cancel() => _cancel = true;
    public void Dispose() => Cancel();
}
