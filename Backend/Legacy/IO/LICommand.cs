using System.Collections.Generic;
using System.Linq;

namespace LargerInventory.Backend.IO;

public abstract class LICommand
{
    public ICollection<ItemFilter> Filters;
    public bool AllowOptimization = true;
    internal ICollection<int> Candidates = [];

    internal void SelectCandidates(ItemSummary[] summaries)
    {
        foreach (var summary in summaries)
        {
            if (Filters.All(filter => filter.Filter(summary)))
            {
                Candidates.Add(summary.Identity);
            }
        }
    }
}