using Terraria;

namespace LargerInventory.Backend.Storage;

/// <summary>
/// 一个物理页：1024 个槽位。槽位始终持有非 null 的 Item 引用；
/// 空物品是 type=0 的默认 Item（每个空槽独立实例）。
/// 页实体可被卸载（Slots=null），摘要（Summary）常驻。
/// </summary>
internal sealed class ItemPage
{
    public const int SlotCount = 1024;

    public readonly int PageIndex;
    public readonly PageSummary Summary = new();

    /// <summary>页内槽位。未加载时为 null；加载后长度恒为 1024，元素非 null。</summary>
    public Item[] Slots;

    /// <summary>volatile：修复服务在后台读取，避免看到撕裂状态。</summary>
    public volatile bool Dirty;

    /// <summary>volatile：后台修复服务据此跳过已加载页。</summary>
    public volatile bool RuntimeLoaded;

    /// <summary>volatile：该页已知包含物品（摘要非空），缺失文件时不能当作空页。</summary>
    public volatile bool KnownNonEmpty;

    /// <summary>最近一次加载失败（文件损坏或缺失但摘要非空）。此类页不会写入元数据，页文件保持原样。</summary>
    public volatile bool LoadFailed;
    public int PinCount;
    public long LastUsedMs;

    /// <summary>该页的摘要是否已经进入全局 TypeIndex。</summary>
    public bool Indexed;

    public ItemPage(int pageIndex)
    {
        PageIndex = pageIndex;
    }

    public bool Loaded => Slots is not null;

    public Item[] EnsureSlots()
    {
        if (Slots is not null)
        {
            return Slots;
        }

        var slots = new Item[SlotCount];
        for (var i = 0; i < SlotCount; i++)
        {
            slots[i] = new Item();
        }

        Slots = slots;
        RuntimeLoaded = true;
        return slots;
    }

    /// <summary>卸载页实体，只保留摘要。</summary>
    public void ReleaseSlots()
    {
        Slots = null;
        RuntimeLoaded = false;
    }

    /// <summary>整槽取走后复位为新的默认 Item（不复用同一实例）。</summary>
    public void ResetSlot(int index)
    {
        Slots[index] = new Item();
    }
}
