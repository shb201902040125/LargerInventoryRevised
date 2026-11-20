using System;
using System.Collections.Generic;

namespace LargerInventory.Backend.Storage;

/// <summary>全局类型聚合索引。内部精确计数，仅在查询输出时饱和。</summary>
internal sealed class TypeIndex
{
    private sealed class Entry
    {
        public long Count;
        public long Favorites;
        public readonly HashSet<int> Pages = new();
    }

    private readonly Dictionary<int, Entry> _map = new();

    public int TypeCount => _map.Count;

    public bool TryGet(int type, out int count, out int favorites, out int[] pages)
        => TryGet(type, out count, out favorites, out pages, out _);

    public bool TryGet(int type, out int count, out int favorites, out int[] pages, out bool saturated)
    {
        count = 0;
        favorites = 0;
        saturated = false;
        pages = Array.Empty<int>();
        if (!_map.TryGetValue(type, out var entry))
        {
            return false;
        }

        count = (int)Math.Min(entry.Count, int.MaxValue);
        favorites = (int)Math.Min(entry.Favorites, int.MaxValue);
        saturated = entry.Count > int.MaxValue || entry.Favorites > int.MaxValue;
        pages = new int[entry.Pages.Count];
        entry.Pages.CopyTo(pages);
        Array.Sort(pages);
        return true;
    }

    public bool HasType(int type) => _map.ContainsKey(type);

    public void UpdatePage(int pageIndex, Dictionary<int, TypeStat> before, Dictionary<int, TypeStat> after)
    {
        foreach (var pair in before)
        {
            after.TryGetValue(pair.Key, out var now);
            ApplyDelta(pair.Key, now.ExactCount - pair.Value.ExactCount,
                now.ExactFavorites - pair.Value.ExactFavorites, pageIndex, now.ExactCount > 0);
        }

        foreach (var pair in after)
        {
            if (!before.ContainsKey(pair.Key))
            {
                ApplyDelta(pair.Key, pair.Value.ExactCount, pair.Value.ExactFavorites, pageIndex, true);
            }
        }
    }

    private void ApplyDelta(int type, long countDelta, long favoritesDelta, int pageIndex, bool pageStillHasType)
    {
        if (!_map.TryGetValue(type, out var entry))
        {
            if (!pageStillHasType)
            {
                return;
            }
            entry = new Entry();
            _map[type] = entry;
        }

        entry.Count += countDelta;
        entry.Favorites += favoritesDelta;
        if (pageStillHasType)
        {
            entry.Pages.Add(pageIndex);
        }
        else
        {
            entry.Pages.Remove(pageIndex);
            if (entry.Pages.Count == 0)
            {
                _map.Remove(type);
            }
        }
    }

    public void Clear() => _map.Clear();
}
