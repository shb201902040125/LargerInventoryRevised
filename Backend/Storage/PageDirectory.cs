using System;
using System.Collections.Generic;

namespace LargerInventory.Backend.Storage;

/// <summary>
/// 页目录：根数组（2048）→ 页块（1024）→ ItemPage。
/// 按需分配页块，不预分配 2^21 个页对象。
/// </summary>
internal sealed class PageDirectory
{
    public const int BlockCount = 2048;
    public const int PagesPerBlock = 1024;
    public const int MaxPageCount = BlockCount * PagesPerBlock;

    private readonly ItemPage[][] _blocks = new ItemPage[BlockCount][];
    private readonly List<ItemPage> _allocated = new();
    private int _nextPageIndex;

    public IReadOnlyList<ItemPage> Allocated => _allocated;

    public ItemPage Allocate()
    {
        if (_nextPageIndex >= MaxPageCount)
        {
            throw new InvalidOperationException("Page capacity exhausted (int SlotId limit reached).");
        }

        return GetOrCreate(_nextPageIndex++);
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
