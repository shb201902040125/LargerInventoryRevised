using System.Diagnostics.CodeAnalysis;
using Terraria;

namespace LargerInventory.Backend.IO;

public struct ItemSummary
{
    internal int Identity;
    internal int Type;
    internal int Stack;
    internal bool favorited;
    internal Item item;
    internal bool loaded;
}