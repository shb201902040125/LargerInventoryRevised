using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;

namespace LargerInventory.Backend.Crafting;

/// <summary>Recipe indices refer to Main.recipe[0..Recipe.numRecipes]. Each result tests one craft independently.</summary>
internal sealed class RecipeCatalog
{
    private const int MaxWorkers = 4;
    private readonly MaterialRecipe[] _materials;
    private readonly Recipe[] _engineRecipes;
    private readonly byte[] _fingerprint;

    internal int Count => _materials.Length;
    internal byte[] Fingerprint => (byte[])_fingerprint.Clone();

    internal readonly record struct Ingredient(int Type, int Stack);
    internal sealed record Group(string[] Names, int[] Members);
    internal sealed record Definition(Ingredient Output, Ingredient[] Inputs, int[] Tiles,
        Group[] Groups, bool Disabled, string[] ConditionKeys);

    /// <summary>Capture after tML finishes sorting and setting up recipes, on the game thread.</summary>
    internal static RecipeCatalog Capture()
    {
        var engineRecipes = new Recipe[Recipe.numRecipes];
        Array.Copy(Main.recipe, engineRecipes, engineRecipes.Length);
        var groupNames = RecipeGroup.recipeGroupIDs.ToLookup(entry => entry.Value, entry => entry.Key);
        var groups = new Dictionary<int, Group>();
        Group CaptureGroup(int id)
        {
            if (!groups.TryGetValue(id, out var group))
            {
                group = new Group(groupNames[id].Order(StringComparer.Ordinal).ToArray(),
                    RecipeGroup.recipeGroups[id].ValidItems.ToArray());
                groups.Add(id, group);
            }

            return group;
        }

        static Ingredient Describe(Item item) => new(item.type, item.stack);
        var definitions = new Definition[engineRecipes.Length];
        for (int i = 0; i < definitions.Length; i++)
        {
            Recipe recipe = engineRecipes[i];
            definitions[i] = new Definition(Describe(recipe.createItem), recipe.requiredItem.Select(Describe).ToArray(),
                recipe.requiredTile.ToArray(), recipe.acceptedGroups.Select(CaptureGroup).ToArray(), recipe.Disabled,
                recipe.Conditions.Select(condition => condition.Description?.Key ?? string.Empty).ToArray());
        }

        return new RecipeCatalog(definitions, ItemKey, TileKey, engineRecipes);
    }

    internal RecipeCatalog(Definition[] definitions, Func<int, string> itemKey, Func<int, string> tileKey)
        : this(definitions, itemKey, tileKey, null)
    {
    }

    private RecipeCatalog(Definition[] definitions, Func<int, string> itemKey, Func<int, string> tileKey,
        Recipe[] engineRecipes)
    {
        _engineRecipes = engineRecipes;
        _materials = definitions.Select(definition => new MaterialRecipe(definition)).ToArray();
        _fingerprint = ComputeFingerprint(definitions, itemKey, tileKey);
    }

    private static string ItemKey(int type) => ModContent.GetModItem(type)?.FullName ??
        (ItemID.Search.ContainsId(type) ? "Terraria/" + ItemID.Search.GetName(type) : UnknownId(type));

    private static string TileKey(int type) => ModContent.GetModTile(type)?.FullName ??
        (TileID.Search.ContainsId(type) ? "Terraria/" + TileID.Search.GetName(type) : UnknownId(type));

    private static string UnknownId(int type) => "#" + type.ToString(CultureInfo.InvariantCulture);

    // Versioned, length-prefixed UTF-8 and little-endian integers. Preserve the indexed recipe order
    // and ingredient, station, accepted-group and condition order. Sort group names and membership
    // with ordinal comparison because their engine dictionaries/sets have no canonical order.
    // Condition keys cannot identify delegate code, closures or localized format arguments. This
    // also omits consumption/craft hooks, item instance data, decrafting and dynamic world state.
    private static byte[] ComputeFingerprint(Definition[] definitions, Func<int, string> itemKey,
        Func<int, string> tileKey)
    {
        using var hash = SHA256.Create();
        using var stream = new CryptoStream(Stream.Null, hash, CryptoStreamMode.Write);
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        var groupKeys = new Dictionary<Group, string[]>();
        writer.Write("LargerInventory.RecipeCatalog/1");
        writer.Write(definitions.Length);
        foreach (Definition definition in definitions)
        {
            writer.Write(definition.Disabled);
            writer.Write(itemKey(definition.Output.Type));
            writer.Write(definition.Output.Stack);
            writer.Write(definition.Inputs.Length);
            foreach (Ingredient input in definition.Inputs)
            {
                writer.Write(itemKey(input.Type));
                writer.Write(input.Stack);
            }

            writer.Write(definition.Tiles.Length);
            foreach (int tile in definition.Tiles)
                writer.Write(tileKey(tile));

            writer.Write(definition.Groups.Length);
            foreach (Group group in definition.Groups)
            {
                WriteStrings(writer, group.Names.Order(StringComparer.Ordinal).ToArray());
                if (!groupKeys.TryGetValue(group, out var members))
                {
                    members = group.Members.Select(itemKey).Order(StringComparer.Ordinal).ToArray();
                    groupKeys.Add(group, members);
                }
                WriteStrings(writer, members);
            }

            WriteStrings(writer, definition.ConditionKeys);
        }

        writer.Flush();
        stream.FlushFinalBlock();
        return hash.Hash;
    }

    private static void WriteStrings(BinaryWriter writer, string[] values)
    {
        writer.Write(values.Length);
        foreach (string value in values)
            writer.Write(value);
    }

    internal bool[] CaptureConditions() => CaptureConditions(null);

    /// <summary>
    /// Capture on the game thread. A dedicated server may supply the authenticated, server-side
    /// Player for stations and supported player conditions. See RecipeConditionSnapshot for limitations.
    /// </summary>
    internal bool[] CaptureConditions(Player authorityPlayer)
    {
        if (_engineRecipes == null)
            throw new InvalidOperationException("The catalog has no engine recipes.");

        return RecipeConditionSnapshot.Capture(_engineRecipes, authorityPlayer);
    }

    /// <summary>
    /// Pure evaluation over caller-owned stable snapshots. Both inputs must support concurrent reads
    /// and remain unchanged until return. Padding bits are zero and recipe indices are LSB-first.
    /// </summary>
    internal byte[] Evaluate(IReadOnlyDictionary<int, long> counts, bool[] conditions,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(counts);
        ArgumentNullException.ThrowIfNull(conditions);
        if (conditions.Length != Count)
            throw new ArgumentException("Condition snapshot must contain one entry per recipe.", nameof(conditions));
        cancellationToken.ThrowIfCancellationRequested();

        var bitmap = new byte[(Count + 7L) / 8];
        void EvaluateByte(int offset)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int first = offset * 8;
            int length = Math.Min(8, Count - first);
            byte value = 0;
            for (int bit = 0; bit < length; bit++)
            {
                int index = first + bit;
                if (conditions[index] && _materials[index].CanCraft(counts, cancellationToken))
                    value |= (byte)(1 << bit);
            }
            bitmap[offset] = value;
        }

        int workers = Math.Min(MaxWorkers, Environment.ProcessorCount);
        if (workers == 1 || bitmap.Length < 32)
        {
            for (int offset = 0; offset < bitmap.Length; offset++)
                EvaluateByte(offset);
        }
        else
        {
            // A byte has exactly one writer, so adjacent recipe bits never need atomic updates.
            Parallel.For(0, bitmap.Length, new ParallelOptions
            {
                MaxDegreeOfParallelism = workers,
                CancellationToken = cancellationToken
            }, EvaluateByte);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return bitmap;
    }

    private sealed class MaterialRecipe
    {
        private readonly bool _disabled;
        private readonly (int Type, long Demand)[] _direct;
        private readonly bool _hasSubstitutes;
        private readonly bool _overlapping;
        private readonly (int[] Types, long Demand)[] _requirements;
        private readonly long _totalDemand;

        internal MaterialRecipe(Definition definition)
        {
            _disabled = definition.Disabled;
            // In tML, a substitute is accepted if it shares any recipe-level accepted group with
            // the required item (AcceptedByItemGroups). Membership is neither iconic-item-only
            // nor transitive across groups. A direct match is always included.
            var demands = definition.Inputs.Where(input => input.Type > 0 && input.Stack > 0)
                .GroupBy(input => input.Type)
                .Select(inputs => (Type: inputs.Key, Demand: inputs.Sum(input => (long)input.Stack))).ToArray();
            _direct = demands;
            if (definition.Groups.Length == 0)
                return;

            _requirements = demands.Select(input =>
                (Types: definition.Groups.Where(group => Array.IndexOf(group.Members, input.Type) >= 0)
                    .SelectMany(group => group.Members).Where(type => type > 0).Append(input.Type).Distinct().ToArray(),
                 input.Demand)).ToArray();
            _totalDemand = _requirements.Sum(requirement => requirement.Demand);
            var seen = new HashSet<int>();
            foreach (var requirement in _requirements)
            {
                _hasSubstitutes |= requirement.Types.Length > 1;
                foreach (int type in requirement.Types)
                    _overlapping |= !seen.Add(type);
            }
        }

        internal bool CanCraft(IReadOnlyDictionary<int, long> counts, CancellationToken cancellationToken)
        {
            if (_disabled)
                return false;

            // Direct requirements were merged by type, so sufficient direct stock is already a valid allocation.
            bool direct = true;
            foreach (var (type, demand) in _direct)
            {
                if (counts.GetValueOrDefault(type) >= demand) continue;
                direct = false;
                break;
            }
            if (direct || !_hasSubstitutes)
                return direct;

            // Disjoint candidate sets can be satisfied independently. For overlapping sets this
            // check can only rule a recipe out, so sufficient stock still goes through max flow.
            foreach (var (types, demand) in _requirements)
            {
                long remaining = demand;
                foreach (int type in types)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    remaining -= Math.Min(remaining, Math.Max(0, counts.GetValueOrDefault(type)));
                    if (remaining == 0) break;
                }
                if (remaining != 0) return false;
            }
            return !_overlapping || CanAllocate(counts, cancellationToken);
        }

        private bool CanAllocate(IReadOnlyDictionary<int, long> counts, CancellationToken cancellationToken)
        {
            // Source -> each stocked type -> compatible requirement -> sink. A max flow
            // saturating all requirement edges is exactly one feasible allocation.
            const int source = 0;
            const int sink = 1;
            const int firstRequirement = 2;
            var flow = new AllocationFlow(firstRequirement + _requirements.Length);
            var supplyNodes = new Dictionary<int, int>();
            for (int i = 0; i < _requirements.Length; i++)
            {
                int requirement = firstRequirement + i;
                var (types, demand) = _requirements[i];
                long shortfall = demand;
                flow.AddEdge(requirement, sink, demand);
                foreach (int type in types)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    long supply = counts.GetValueOrDefault(type);
                    if (supply <= 0)
                        continue;
                    shortfall -= Math.Min(shortfall, supply);
                    if (!supplyNodes.TryGetValue(type, out int node))
                    {
                        node = flow.AddNode();
                        supplyNodes.Add(type, node);
                        flow.AddEdge(source, node, Math.Min(_totalDemand, supply));
                    }
                    flow.AddEdge(node, requirement, Math.Min(demand, supply));
                }
                if (shortfall != 0)
                    return false;
            }

            return flow.CanSend(source, sink, _totalDemand, cancellationToken);
        }
    }

    // Iterative Edmonds-Karp. Each pass finds a shortest residual path and moves a full
    // bottleneck capacity. Runtime is polynomial in the graph size, independent of stack sizes.
    private sealed class AllocationFlow
    {
        private struct Edge(int to, int reverse, long remaining)
        {
            internal readonly int To = to;
            internal readonly int Reverse = reverse;
            internal long Remaining = remaining;
        }

        private readonly List<List<Edge>> _nodes = new();

        internal AllocationFlow(int nodeCount)
        {
            for (int i = 0; i < nodeCount; i++)
                AddNode();
        }

        internal int AddNode()
        {
            _nodes.Add(new List<Edge>());
            return _nodes.Count - 1;
        }

        internal void AddEdge(int from, int to, long capacity)
        {
            var forward = new Edge(to, _nodes[to].Count, capacity);
            _nodes[to].Add(new Edge(from, _nodes[from].Count, 0));
            _nodes[from].Add(forward);
        }

        internal bool CanSend(int source, int sink, long demand, CancellationToken cancellationToken)
        {
            var predecessors = new int[_nodes.Count];
            var incoming = new int[_nodes.Count];
            var queue = new int[_nodes.Count];
            while (demand > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Array.Fill(predecessors, -1);
                predecessors[source] = source;
                queue[0] = source;
                int tail = 1;
                for (int head = 0; head < tail && predecessors[sink] == -1; head++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int from = queue[head];
                    var edges = CollectionsMarshal.AsSpan(_nodes[from]);
                    for (int index = 0; index < edges.Length; index++)
                    {
                        ref readonly Edge edge = ref edges[index];
                        if (edge.Remaining <= 0 || predecessors[edge.To] != -1)
                            continue;
                        predecessors[edge.To] = from;
                        incoming[edge.To] = index;
                        if (edge.To == sink)
                            break;
                        queue[tail++] = edge.To;
                    }
                }

                if (predecessors[sink] == -1)
                    return false;

                long amount = demand;
                for (int to = sink; to != source; to = predecessors[to])
                    amount = Math.Min(amount, _nodes[predecessors[to]][incoming[to]].Remaining);
                for (int to = sink; to != source; to = predecessors[to])
                {
                    ref Edge edge = ref CollectionsMarshal.AsSpan(_nodes[predecessors[to]])[incoming[to]];
                    edge.Remaining -= amount;
                    CollectionsMarshal.AsSpan(_nodes[to])[edge.Reverse].Remaining += amount;
                }
                demand -= amount;
            }
            return true;
        }
    }
}
