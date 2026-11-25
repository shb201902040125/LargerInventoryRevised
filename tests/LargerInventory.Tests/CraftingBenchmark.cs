using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Security.Cryptography;
using LargerInventory.Backend.Crafting;
using static LargerInventory.Backend.Crafting.RecipeCatalog;

namespace LargerInventory.Tests;

internal static partial class Program
{
    private static int RunCraftingBenchmark()
    {
        const int recipeCount = 12000;
        const int iterations = 120;
        Console.WriteLine($"Runtime {Environment.Version}, logical processors {Environment.ProcessorCount}, {iterations} queries per sample");
        Measure("Exact", i => CraftingRecipe([new(i % 128 + 1, 2), new(130, 4)]));
        Measure("Disjoint groups", i => CraftingRecipe([new(1, 2), new(3, 4), new(i % 64 + 64, 1)],
            [new Group(["A"], [1, 2]), new Group(["B"], [3, 4])]));
        Measure("Overlapping, direct stock", i => CraftingRecipe([new(1, 2), new(3, 4), new(i % 64 + 64, 1)],
            [new Group(["A"], [1, 2]), new Group(["B"], [2, 3])]));
        Measure("Overlapping, allocation needed", i => CraftingRecipe([new(1, 2), new(4, 2), new(i % 64 + 64, 1)],
            [new Group(["A"], [1, 2]), new Group(["B"], [2, 3, 4])]));
        return 0;

        static void Measure(string name, Func<int, Definition> recipe)
        {
            var catalog = CraftingCatalog(Enumerable.Range(0, recipeCount).Select(recipe).ToArray());
            var counts = new Dictionary<int, long>();
            for (int type = 1; type <= 130; type++)
                counts[type] = type == 4 ? 1 : type % 5 == 0 ? 0 : 4;
            bool[] conditions = Enumerable.Range(0, recipeCount).Select(i => i % 7 != 0).ToArray();
            byte[] bitmap = null;
            for (int i = 0; i < 25; i++)
                bitmap = catalog.Evaluate(counts, conditions);
            var digest = Convert.ToHexString(SHA256.HashData(bitmap))[..16];
            var timings = new double[3];
            var allocations = new long[3];
            for (int sample = 0; sample < timings.Length; sample++)
            {
                long before = GC.GetTotalAllocatedBytes(precise: true);
                long started = Stopwatch.GetTimestamp();
                for (int i = 0; i < iterations; i++)
                    bitmap = catalog.Evaluate(counts, conditions);
                timings[sample] = Stopwatch.GetElapsedTime(started).TotalMilliseconds / iterations;
                allocations[sample] = (GC.GetTotalAllocatedBytes(precise: true) - before) / iterations;
            }
            Array.Sort(timings);
            Array.Sort(allocations);
            Console.WriteLine($"{name}: {timings[1]:F3} ms/query, {allocations[1]:N0} B/query, SHA256 {digest}");
            GC.KeepAlive(bitmap);
        }
    }
}
