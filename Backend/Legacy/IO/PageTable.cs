using System.Collections.Generic;
using System.IO;

namespace LargerInventory.Backend.IO;

public class PageTable
{
    private HashSet<int> _loadedPages = [];
    private Dictionary<int, DataPage> _pages = [];
    private static string PageFileRoot;

    internal bool TryLoadPage(int index)
    {
        if (_loadedPages.Contains(index))
        {
            return true;
        }

        var newPage = new DataPage(Path.Combine(PageFileRoot,$"{index}.lid"));
        if (!newPage.TryLoadView())
        {
            return false;
        }
        _pages[index] = newPage;
        return true;
    }
}