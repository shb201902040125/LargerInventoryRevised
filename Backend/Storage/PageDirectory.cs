using System;
using System.Collections.Generic;

namespace LargerInventory.Backend.Storage;

/// <summary>
/// 页目录：根数组（2048）→ 页块（1024）→ ItemPage，按需分配页块。
/// 分配器只返回目录中尚不存在的页索引；已存在的页（包括从磁盘恢复的页）永远不会被再次分配，
/// 否则会把已有物品的页当作空页覆盖。
/// </summary>
internal sealed class PageDirectory
{
    public const int BlockCount = 2048;
    public const int PagesPerBlock = 1024;
    public const int MaxPageCount = BlockCount * PagesPerBlock;

    private readonly ItemPage[][] _blocks = new ItemPage[BlockCount][];
    private readonly List<ItemPage> _allocated = new();
    private readonly HashSet<int> _reserved = new();
    private int _allocationCursor;

    public IReadOnlyList<ItemPage> Allocated => _allocated;

    public ItemPage Allocate() => TryAllocate() ??
        throw new InvalidOperationException("Page capacity exhausted (int SlotId limit reached).");

    internal ItemPage TryAllocate()
    {
        for (var attempts = 0; attempts < MaxPageCount; attempts++)
        {
            if (_allocationCursor >= MaxPageCount)
            {
                _allocationCursor = 0;
            }

            var candidate = _allocationCursor++;
            if (!Contains(candidate) && !_reserved.Contains(candidate))
            {
                return GetOrCreate(candidate);
            }
        }

        return null;
    }

    internal void RemoveNewPage(ItemPage page)
    {
        _blocks[page.PageIndex >> 10][page.PageIndex & (PagesPerBlock - 1)] = null;
        _allocated.Remove(page);
        _allocationCursor = Math.Min(_allocationCursor, page.PageIndex);
    }

    public bool Contains(int pageIndex)
    {
        return TryGet(pageIndex, out _);
    }

    /// <summary>预留一个仅存在于磁盘、尚未进入目录的页索引，避免修复期间被分配器复用。</summary>
    public void Reserve(int pageIndex)
    {
        CheckIndex(pageIndex);
        _reserved.Add(pageIndex);
    }

    public void Unreserve(int pageIndex)
    {
        _reserved.Remove(pageIndex);
    }

    public ItemPage GetOrCreate(int pageIndex)
    {
        CheckIndex(pageIndex);
        var block = _blocks[pageIndex >> 10];
        if (block is null)
        {
            block = new ItemPage[PagesPerBlock];
            _blocks[pageIndex >> 10] = block;
        }

        var page = block[pageIndex & (PagesPerBlock - 1)];
        if (page is null)
        {
            page = new ItemPage(pageIndex);
            block[pageIndex & (PagesPerBlock - 1)] = page;
            _allocated.Add(page);
        }

        return page;
    }

    public bool TryGet(int pageIndex, out ItemPage page)
    {
        page = null;
        if (pageIndex < 0 || pageIndex >= MaxPageCount)
        {
            return false;
        }

        var block = _blocks[pageIndex >> 10];
        if (block is null)
        {
            return false;
        }

        page = block[pageIndex & (PagesPerBlock - 1)];
        return page is not null;
    }

    private static void CheckIndex(int pageIndex)
    {
        if (pageIndex < 0 || pageIndex >= MaxPageCount)
        {
            throw new ArgumentOutOfRangeException(nameof(pageIndex), pageIndex, "Page index out of range.");
        }
    }
}
