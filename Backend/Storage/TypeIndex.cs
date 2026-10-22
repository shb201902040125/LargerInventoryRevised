using System;
using System.Collections.Generic;

namespace LargerInventory.Backend.Storage;

/// <summary>
/// 全局类型聚合索引：类型 → 总量、收藏堆数、所在页。
/// 批次结束时由受影响页的摘要差分更新；计数 int 饱和。
/// </summary>
internal sealed class TypeIndex
{
    private sealed class Entry
    {
        public int Count;
        public int Favorites;
        public readonly HashSet<int> Pages = new();
    }

    private readonly Dictionary<int, Entry> _map = new();

    public int TypeCount => _map.Count;

    public bool TryGet(int type, out int count, out int favorites, out int[] pages)
    {
        count = 0;
        favorites = 0;
        pages = Array.Empty<int>();
        if (!_map.TryGetValue(type, out var entry))
        {
            return false;
        }

        count = entry.Count;
        favorites = entry.Favorites;
        pages = new int[entry.Pages.Count];
        entry.Pages.CopyTo(pages);
        Array.Sort(pages);
        return true;
    }

    public bool HasType(int type)
    {
        return _map.ContainsKey(type);
    }

    public void UpdatePage(int pageIndex, Dictionary<int, TypeStat> before, Dictionary<int, TypeStat> after)
    {
        foreach (var pair in before)
        {
            after.TryGetValue(pair.Key, out var now);
            ApplyDelta(pair.Key, now.Count - pair.Value.Count, now.Favorites - pair.Value.Favorites, pageIndex, now.Count > 0);
        }

        foreach (var pair in after)
        {
            if (before.ContainsKey(pair.Key))
            {
                continue;
            }

            ApplyDelta(pair.Key, pair.Value.Count, pair.Value.Favorites, pageIndex, true);
        }
    }

    private void ApplyDelta(int type, int countDelta, int favoritesDelta, int pageIndex, bool pageStillHasType)
    {
        if (countDelta == 0 && favoritesDelta == 0 && !pageStillHasType)
        {
            if (!_map.TryGetValue(type, out var existing))
            {
                return;
            }

            existing.Pages.Remove(pageIndex);
            Cleanup(type, existing);
            return;
        }

        if (!_map.TryGetValue(type, out var entry))
        {
            entry = new Entry();
            _map[type] = entry;
        }

        if (countDelta > 0)
        {
            entry.Count = TypeStat.SatAdd(entry.Count, countDelta);
        }
        else if (countDelta < 0)
        {
            entry.Count = TypeStat.SatSub(entry.Count, -countDelta);
        }

        if (favoritesDelta > 0)
        {
            entry.Favorites = TypeStat.SatAdd(entry.Favorites, favoritesDelta);
        }
        else if (favoritesDelta < 0)
        {
            entry.Favorites = TypeStat.SatSub(entry.Favorites, -favoritesDelta);
        }

        if (pageStillHasType)
        {
            entry.Pages.Add(pageIndex);
        }
        else
        {
            entry.Pages.Remove(pageIndex);
        }

        Cleanup(type, entry);
    }

    private void Cleanup(int type, Entry entry)
    {
        if (entry.Count <= 0 && entry.Favorites <= 0 && entry.Pages.Count == 0)
        {
            _map.Remove(type);
        }
    }

    public void Clear()
    {
        _map.Clear();
    }
}
