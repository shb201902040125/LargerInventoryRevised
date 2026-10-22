namespace LargerInventory.Backend.IO;

public abstract class ItemFilter
{
    public abstract bool Filter(ItemSummary summary);
}