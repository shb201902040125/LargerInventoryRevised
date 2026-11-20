using System;
using System.Collections.Generic;
using System.Linq;
using Terraria;

namespace LargerInventory.Backend.Storage;

/// <summary>
/// 页缓存：负责页的加载、pin、LRU 淘汰与摘要常驻。
/// 只有执行线程访问；主线程通过指令层间接访问。
/// </summary>
internal sealed class PageCache
{
    private readonly StoreConfig _config;
    private readonly IPageFileStore _fileStore;
    private readonly PageLockTable _pageLocks;
    private readonly PageDirectory _directory = new();
    private readonly HashSet<int> _pinned = new();
    private readonly HashSet<ItemPage> _dirty = new();
    private readonly HashSet<ItemPage> _unsnapshotted = new();
    private readonly Dictionary<int, (int Version, byte[] Bytes)> _snapshots = new();
    private readonly HashSet<int> _backupLoads = new();

    public PageCache(StoreConfig config, IPageFileStore fileStore, PageLockTable pageLocks)
    {
        _config = config;
        _fileStore = fileStore ?? new NullPageFileStore();
        _pageLocks = pageLocks ?? new PageLockTable();
    }

    public PageDirectory Directory => _directory;

    public IReadOnlyList<ItemPage> AllocatedPages => _directory.Allocated;

    public ItemPage EnsureLoaded(int pageIndex)
    {
        var page = _directory.GetOrCreate(pageIndex);
        if (page.Loaded)
        {
            page.LastUsedMs = NowMs();
            return page;
        }

        // 与修复服务/其它调用共享页锁；主线程指令通常已持有该锁（Monitor 可重入）。
        if (!_pageLocks.TryEnter(pageIndex))
        {
            throw new PageLoadException($"page {pageIndex} is busy");
        }

        try
        {
            try
            {
                LoadPage(page);
            }
            catch (PageLoadException)
            {
                page.LoadFailed = true;
                throw;
            }

            page.LoadFailed = false;
            page.LastUsedMs = NowMs();
            return page;
        }
        finally
        {
            _pageLocks.Exit(pageIndex);
        }
    }

    public ItemPage AllocatePage(bool allowCapacityFailure = false)
    {
        var page = allowCapacityFailure ? _directory.TryAllocate() : _directory.Allocate();
        if (page is null)
            return null;
        page.EnsureSlots();
        page.Summary.RebuildFrom(page.Slots);
        page.KnownNonEmpty = false;
        page.LastUsedMs = NowMs();
        return page;
    }

    internal void RemoveNewPage(ItemPage page)
    {
        _pinned.Remove(page.PageIndex);
        ClearDirty(page);
        _backupLoads.Remove(page.PageIndex);
        _directory.RemoveNewPage(page);
    }

    public bool TryGetLoaded(int pageIndex, out ItemPage page)
    {
        page = null;
        return _directory.TryGet(pageIndex, out page) && page.Loaded;
    }

    public void Pin(int pageIndex)
    {
        if (!_pinned.Add(pageIndex))
        {
            return;
        }

        if (_directory.TryGet(pageIndex, out var page))
        {
            page.PinCount++;
        }
    }

    public void UnpinAll()
    {
        foreach (var pageIndex in _pinned)
        {
            if (_directory.TryGet(pageIndex, out var page) && page.PinCount > 0)
            {
                page.PinCount--;
            }
        }

        _pinned.Clear();
        MaybeEvict();
    }

    /// <summary>按配置上限修剪当前已加载页；用于启动时逐页重建摘要。</summary>
    public void TrimToLimit()
    {
        MaybeEvict();
    }

    public void MarkDirty(ItemPage page)
    {
        _snapshots.Remove(page.PageIndex);
        _dirty.Add(page);
        if (_fileStore.Available)
            _unsnapshotted.Add(page);
        page.Dirty = true;
    }

    private void LoadPage(ItemPage page)
    {
        var result = _fileStore.Available
            ? _fileStore.Load(page.PageIndex)
            : PageLoadResult.Missing();

        if (result.NotFound)
        {
            if (page.Summary.OccupiedStacks > 0)
            {
                throw new PageLoadException($"page file missing for non-empty page {page.PageIndex}");
            }

            page.EnsureSlots();
            page.Summary.RebuildFrom(page.Slots);
            page.KnownNonEmpty = false;
            ClearDirty(page);
            return;
        }

        if (!result.Success)
        {
            throw new PageLoadException(result.Error ?? "unknown page load failure");
        }

        var items = result.Items;
        if (items is null || items.Length != ItemPage.SlotCount)
        {
            throw new PageLoadException($"page {page.PageIndex} returned an invalid item array");
        }

        for (var i = 0; i < items.Length; i++)
        {
            items[i] ??= new Item();
        }

        page.Slots = items;
        page.RuntimeLoaded = true;
        page.Summary.RebuildFrom(items);
        page.KnownNonEmpty = page.Summary.OccupiedStacks > 0;

        if (result.FromBackup)
            _backupLoads.Add(page.PageIndex);
        else
            _backupLoads.Remove(page.PageIndex);

        page.Summary.Version++;
        ClearDirty(page);
    }

    private void MaybeEvict()
    {
        if (!_config.EnablePageEviction || !_fileStore.Available)
        {
            return;
        }

        var now = NowMs();
        var loaded = _directory.Allocated.Where(p => p.Loaded).ToList();

        foreach (var page in loaded)
        {
            if (page.PinCount > 0)
            {
                continue;
            }

            var idle = now - page.LastUsedMs;
            var overLimit = loaded.Count(p => p.Loaded) > _config.MaxLoadedPages;
            if (!overLimit && idle < _config.PageIdleUnloadMs)
            {
                continue;
            }

            if (page.Dirty)
                continue;

            page.ReleaseSlots();
        }
    }

    /// <summary>Only the owner thread calls ItemIO. Save threads use the resulting byte arrays.</summary>
    public void CaptureDirtySnapshots()
    {
        if (_unsnapshotted.Count == 0)
            return;

        // Snapshot hooks may fail. Keep those pages in the set for the next owner-thread attempt.
        foreach (var page in _unsnapshotted.ToArray())
        {
            try
            {
                _snapshots[page.PageIndex] = (page.Summary.Version,
                    FilePageFileStore.Snapshot(page.PageIndex, page.Slots, page.Summary));
                _unsnapshotted.Remove(page);
            }
            catch (Exception e)
            {
                _snapshots.Remove(page.PageIndex);
                Log.Error($"Could not snapshot page {page.PageIndex}.", e);
            }
        }
    }

    public bool HasDirtyPages => _dirty.Count != 0;

    public bool WasLoadedFromBackup(int index) => _backupLoads.Contains(index);

    public void ClearBackupSource(int index) => _backupLoads.Remove(index);

    public (ItemPage[] Pages, FilePageFileStore.SaveSnapshot[] Files) FreezeDirtyPages()
    {
        var pages = new ItemPage[_dirty.Count];
        var files = new FilePageFileStore.SaveSnapshot[pages.Length];
        var i = 0;
        foreach (var page in _dirty)
        {
            if (!page.Loaded || !_snapshots.TryGetValue(page.PageIndex, out var snapshot) || snapshot.Version != page.Summary.Version)
                throw new InvalidOperationException($"Page {page.PageIndex} has no current main-thread snapshot.");
            pages[i] = page;
            files[i++] = new(page.PageIndex, snapshot.Bytes, _backupLoads.Contains(page.PageIndex));
        }
        return (pages, files);
    }

    public void AcknowledgeCommit(ItemPage[] pages)
    {
        foreach (var page in pages)
        {
            ClearDirty(page);
            _backupLoads.Remove(page.PageIndex);
        }
    }

    private void ClearDirty(ItemPage page)
    {
        page.Dirty = false;
        _dirty.Remove(page);
        _unsnapshotted.Remove(page);
        _snapshots.Remove(page.PageIndex);
    }

    internal static long NowMs()
    {
        return Environment.TickCount64;
    }
}
