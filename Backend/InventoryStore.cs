using System;
using System.Collections.Generic;
using System.IO;
using LargerInventory.Backend.Commands;
using LargerInventory.Backend.Storage;

namespace LargerInventory.Backend;

/// <summary>
/// 储存实例（不是 static）。页缓存 + 类型聚合 + 指令队列 + 执行线程；
/// 绑定一个作用域目录（玩家/世界）时启用每页文件持久化与 store.meta 快速视图。
/// </summary>
public sealed class InventoryStore : IDisposable
{
    public StoreConfig Config { get; }
    public string DirectoryPath { get; }
    public StoreScope Scope { get; }

    internal PageCache PageCache { get; }
    internal TypeIndex TypeIndex { get; }
    internal CommandQueue Queue { get; }

    private readonly CommandExecutor _executor;
    private readonly IPageFileStore _fileStore;
    private bool _started;

    public InventoryStore(StoreConfig config = null)
        : this(config, null, StoreScope.World)
    {
    }

    public InventoryStore(StoreConfig config, string storeDirectory, StoreScope scope = StoreScope.World)
    {
        Config = config ?? new StoreConfig();
        Scope = scope;
        DirectoryPath = storeDirectory;

        if (!string.IsNullOrWhiteSpace(storeDirectory))
        {
            Directory.CreateDirectory(storeDirectory);
            _fileStore = new FilePageFileStore(storeDirectory);
            // 有持久化能力时才允许真正卸载页。
            Config.EnablePageEviction = true;
        }
        else
        {
            _fileStore = new NullPageFileStore();
            Config.EnablePageEviction = false;
        }

        PageCache = new PageCache(Config, _fileStore);
        TypeIndex = new TypeIndex();
        Queue = new CommandQueue();
        _executor = new CommandExecutor(this, Queue);

        LoadQuickViews();
    }

    public void Start()
    {
        if (_started)
        {
            return;
        }

        _started = true;
        _executor.Start();
    }

    /// <summary>压入一条指令。完成通知只通过 Command.Callback；指令层不提供等待/超时。</summary>
    public void Push(Command command)
    {
        if (!_started)
        {
            Start();
        }

        Queue.Push(command);
    }

    /// <summary>
    /// 批量压入：仅保证连续/顺序；不自动追加边界、不强制立即调度。
    /// 需要立即调度时自行再 Push(CommandFactory.Boundary())。
    /// </summary>
    public void PushBatch(IReadOnlyList<Command> commands)
    {
        if (!_started)
        {
            Start();
        }

        Queue.PushBatch(commands);
    }

    /// <summary>存档屏障：暂停执行线程，保存 Dirty 页与 store.meta，然后恢复。</summary>
    public bool FlushForSave(out string error)
    {
        error = null;
        _executor.Pause();
        try
        {
            if (!PageCache.SaveDirtyPages(out error))
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(DirectoryPath))
            {
                StoreMeta.Save(DirectoryPath, PageCache.AllocatedPages);
            }

            return true;
        }
        catch (Exception e)
        {
            error = e.Message;
            Log.Error("FlushForSave failed.", e);
            return false;
        }
        finally
        {
            _executor.Resume();
        }
    }

    internal ItemPage EnsurePage(int pageIndex)
    {
        var page = PageCache.EnsureLoaded(pageIndex);
        if (!page.Indexed && Config.EnableGlobalAggregate)
        {
            TypeIndex.UpdatePage(pageIndex, new Dictionary<int, TypeStat>(), page.Summary.Types);
            page.Indexed = true;
        }

        return page;
    }

    internal ItemPage AllocatePage()
    {
        var page = PageCache.AllocatePage();
        page.Indexed = true;
        return page;
    }

    internal void PinPage(int pageIndex)
    {
        PageCache.Pin(pageIndex);
    }

    internal void MarkDirty(ItemPage page)
    {
        PageCache.MarkDirty(page);
    }

    /// <summary>批次结束后统一重算受影响页的摘要，并更新全局类型聚合。</summary>
    internal void SyncSummaries(HashSet<int> affectedPages)
    {
        foreach (var pageIndex in affectedPages)
        {
            if (!PageCache.Directory.TryGet(pageIndex, out var page) || !page.Loaded)
            {
                continue;
            }

            var before = page.Summary.SnapshotTypes();
            page.Summary.RebuildFrom(page.Slots);
            page.Summary.Version++;
            page.Dirty = true;

            if (Config.EnableGlobalAggregate)
            {
                TypeIndex.UpdatePage(pageIndex, before, page.Summary.Types);
            }
        }
    }

    private void LoadQuickViews()
    {
        if (string.IsNullOrWhiteSpace(DirectoryPath))
        {
            return;
        }

        var summaries = StoreMeta.Load(DirectoryPath);
        foreach (var pair in summaries)
        {
            var page = PageCache.Directory.GetOrCreate(pair.Key);
            page.Summary.CopyFrom(pair.Value);
            page.Indexed = true;

            if (Config.EnableGlobalAggregate)
            {
                TypeIndex.UpdatePage(pair.Key, new Dictionary<int, TypeStat>(), page.Summary.Types);
            }
        }

        if (summaries.Count > 0)
        {
            Log.Info($"Loaded quick views for {summaries.Count} page(s) from {DirectoryPath}.");
        }
    }

    public void Dispose()
    {
        // 尽力保存，避免世界卸载/切换时丢脏页。
        try
        {
            FlushForSave(out var error);
            if (error is not null)
            {
                Log.Error("Dispose FlushForSave failed: " + error);
            }
        }
        catch (Exception e)
        {
            Log.Error("Dispose failed to flush store.", e);
        }

        _executor?.Dispose();
        // 不释放 Queue 的内核事件：执行线程是后台线程，极端情况下可能尚未完全退出。
    }
}
