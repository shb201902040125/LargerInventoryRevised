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
    private readonly PageDirectory _directory = new();
    private readonly HashSet<int> _pinned = new();

    public PageCache(StoreConfig config, IPageFileStore fileStore)
    {
        _config = config;
        _fileStore = fileStore ?? new NullPageFileStore();
    }

    public PageDirectory Directory => _directory;

    public IReadOnlyList<ItemPage> AllocatedPages => _directory.Allocated;

    public ItemPage EnsureLoaded(int pageIndex)
    {
        var page = _directory.GetOrCreate(pageIndex);
        if (!page.Loaded)
        {
            LoadPage(page);
        }

        page.LastUsedMs = NowMs();
        return page;
    }

    public ItemPage AllocatePage()
    {
        var page = _directory.Allocate();
        page.EnsureSlots();
        page.Summary.RebuildFrom(page.Slots);
        page.LastUsedMs = NowMs();
        return page;
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

    public void MarkDirty(ItemPage page)
    {
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
            page.Dirty = false;
            return;
        }

        if (!result.Success)
        {
            throw new PageLoadException(result.Error ?? "unknown page load failure");
        }

        var items = result.Items;
        if (items is null || items.Length != ItemPage.SlotCount)
        {
            items = new Item[ItemPage.SlotCount];
        }

        for (var i = 0; i < items.Length; i++)
        {
            items[i] ??= new Item();
        }

        page.Slots = items;
        page.Summary.RebuildFrom(items);

        // 快速视图随页保存；加载后用 Items 重建并比对（不一致以 Items 为准）。
        if (result.QuickView is not null)
        {
            if (result.QuickView.OccupiedStacks != page.Summary.OccupiedStacks)
            {
                Log.Warn($"Page {page.PageIndex}: quick view occupied count mismatch; rebuilt from items.");
            }

            page.Summary.Version = Math.Max(page.Summary.Version, result.QuickView.Version);
        }

        page.Summary.Version++;
        page.Dirty = false;
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

            if (page.Dirty && !SavePage(page, out var error))
            {
                Log.Error($"Page {page.PageIndex} save before eviction failed: {error}");
                continue;
            }

            page.ReleaseSlots();
        }
    }

    /// <summary>保存单页；失败时返回 false 并保持 Dirty。</summary>
    public bool SavePage(ItemPage page, out string error)
    {
        error = null;
        if (!_fileStore.Available)
        {
            return true;
        }

        if (!page.Loaded)
        {
            return true;
        }

        var result = _fileStore.Save(page.PageIndex, page.Slots, page.Summary);
        if (!result.Success)
        {
            error = result.Error;
            return false;
        }

        page.Dirty = false;
        return true;
    }

    /// <summary>存档屏障：保存所有 Dirty 页。</summary>
    public bool SaveDirtyPages(out string error)
    {
        error = null;
        if (!_fileStore.Available)
        {
            return true;
        }

        foreach (var page in _directory.Allocated)
        {
            if (!page.Loaded || !page.Dirty)
            {
                continue;
            }

            if (!SavePage(page, out var pageError))
            {
                error = $"page {page.PageIndex}: {pageError}";
                return false;
            }
        }

        return true;
    }

    internal static long NowMs()
    {
        return Environment.TickCount64;
    }
}


