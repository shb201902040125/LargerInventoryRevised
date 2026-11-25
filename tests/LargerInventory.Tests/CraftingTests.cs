using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using LargerInventory.Backend;
using LargerInventory.Backend.Commands;
using LargerInventory.Backend.Crafting;
using LargerInventory.Backend.Network;
using static LargerInventory.Backend.Crafting.RecipeCatalog;

namespace LargerInventory.Tests;

internal static partial class Program
{
    private static void RunCraftingTests()
    {
        Run(nameof(Crafting_AllocatesOverlappingSubstitutesExactly));
        Run(nameof(Crafting_MatchesExhaustiveSmallAllocations));
        Run(nameof(Crafting_PacksIndependentRecipeBitsAndHonorsCancellation));
        Run(nameof(Crafting_FingerprintsStableNamesAndOrderedRecipeDefinitions));
        Run(nameof(Crafting_UsesDetachedAvailableWarehouseCounts));
        Run(nameof(Crafting_RejectsInvalidWireBitmaps));
    }

    private static void Crafting_MatchesExhaustiveSmallAllocations()
    {
        var random = new Random(671);
        for (int sample = 0; sample < 500; sample++)
        {
            var inputs = Enumerable.Range(0, random.Next(1, 5))
                .Select(_ => new Ingredient(random.Next(1, 5), random.Next(1, 4))).ToArray();
            var groups = Enumerable.Range(0, random.Next(0, 4))
                .Select(i => new Group([$"Group{i}"], Enumerable.Range(1, 4)
                    .Where(_ => random.Next(2) == 0).ToArray())).ToArray();
            var stock = Enumerable.Range(1, 4).ToDictionary(type => type, _ => (long)random.Next(0, 4));
            var units = inputs.SelectMany(input => Enumerable.Repeat(input.Type, input.Stack)).ToArray();
            bool expected = AssignUnit(0);
            bool actual = CraftingCatalog(CraftingRecipe(inputs, groups)).Evaluate(stock, [true])[0] == 1;
            Expect(actual == expected, $"material allocation differs from exhaustive search for sample {sample}");

            bool AssignUnit(int index)
            {
                if (index == units.Length) return true;
                int required = units[index];
                for (int type = 1; type <= 4; type++)
                {
                    if (stock[type] == 0 || (type != required &&
                        !groups.Any(group => group.Members.Contains(type) && group.Members.Contains(required))))
                        continue;
                    stock[type]--;
                    bool feasible = AssignUnit(index + 1);
                    stock[type]++;
                    if (feasible) return true;
                }
                return false;
            }
        }
    }

    private static void Crafting_AllocatesOverlappingSubstitutesExactly()
    {
        var groups = new[] { new Group(["AB"], [2, 1]), new Group(["BC"], [2, 3]) };
        var catalog = CraftingCatalog(
            CraftingRecipe([new(1, 3), new(3, 3)], groups),
            CraftingRecipe([new(1, 4)], groups),
            CraftingRecipe([new(10, int.MaxValue), new(10, int.MaxValue)]),
            CraftingRecipe([new(10, 3), new(11, 3), new(12, 1)],
                [new Group(["XY"], [10, 11]), new Group(["ZA"], [12, 13])]));
        bool[] conditions = [true, true, true, true];
        var counts = new Dictionary<int, long> { [2] = 3, [1] = 3, [10] = (long)int.MaxValue * 2, [13] = 1 };
        Expect(catalog.Evaluate(counts, conditions)[0] == 15,
            "requirements can reroute overlapping stock, combine substitutes, and exceed int.MaxValue demands");
        counts[1] = 2;
        counts[10]--;
        Expect(catalog.Evaluate(counts, conditions)[0] == 10,
            "shared stock must not satisfy two requirements and exact stacks must be summed in long precision");
        counts[1] = 0;
        counts[2] = 1;
        counts[3] = long.MaxValue;
        counts[10] = 5;
        counts[13] = long.MaxValue;
        Expect(catalog.Evaluate(counts, conditions)[0] == 0,
            "substitution is not transitive and excess stock in a disjoint group cannot cover a shortfall");
    }

    private static void Crafting_PacksIndependentRecipeBitsAndHonorsCancellation()
    {
        const int recipeCount = 265;
        var definitions = new Definition[recipeCount];
        var conditions = new bool[recipeCount];
        for (int i = 0; i < recipeCount; i++)
        {
            definitions[i] = CraftingRecipe([new(1, i % 2 + 1)]) with { Disabled = i == 8 };
            conditions[i] = i != 16;
        }
        var catalog = CraftingCatalog(definitions);
        var counts = new Dictionary<int, long> { [1] = 1 };
        byte[] result = catalog.Evaluate(counts, conditions);
        Expect(result.Length == 34 && result[0] == 0x55 && result[1] == 0x54 && result[2] == 0x54
            && result.Skip(3).Take(30).All(value => value == 0x55) && result[^1] == 1,
            "bitmap must use LSB-first recipe order, skip disabled and unmet conditions, and zero padding");
        Expect(counts[1] == 1, "recipes must evaluate independently and leave the counts untouched");
        Expect(CraftingCatalog().Evaluate(counts, []).Length == 0, "empty catalog must return an empty bitmap");
        try
        {
            catalog.Evaluate(counts, conditions, new CancellationToken(canceled: true));
            Expect(false, "an already canceled evaluation must not return a bitmap");
        }
        catch (OperationCanceledException)
        {
        }
        try
        {
            catalog.Evaluate(counts, [true]);
            Expect(false, "condition snapshot must cover every indexed recipe");
        }
        catch (ArgumentException)
        {
        }
    }

    private static void Crafting_FingerprintsStableNamesAndOrderedRecipeDefinitions()
    {
        var first = CraftingRecipe([new(1, 2)], [new Group(["Test:Group", "Alias"], [1, 2])]) with
        {
            Tiles = [7],
            ConditionKeys = ["Mods.Test.Day"]
        };
        var second = CraftingRecipe([new(2, 3)]);
        var catalog = CraftingCatalog(first, second);
        var expected = catalog.Fingerprint;
        var renumbered = new RecipeCatalog(
            [first with
            {
                Output = new(199, 1), Inputs = [new(101, 2)], Tiles = [107],
                Groups = [new Group(["Alias", "Test:Group"], [102, 101])]
            }, second with { Output = new(199, 1), Inputs = [new(102, 3)] }],
            type => CraftingItemKey(type - 100), type => CraftingTileKey(type - 100));
        Expect(expected.Length == 32 && expected.SequenceEqual(renumbered.Fingerprint),
            "fingerprint must use persistent names and canonicalize group dictionary/set ordering");
        foreach (Definition different in new[]
        {
            first with { Disabled = true }, first with { Inputs = [new(1, 3)] },
            first with { Output = new(99, 2) }, first with { Tiles = [8] },
            first with { ConditionKeys = ["Mods.Test.Night"] },
            first with { Groups = [new Group(["Other"], [1, 2])] },
            first with { Groups = [new Group(["Test:Group", "Alias"], [1, 3])] }
        })
            Expect(!expected.SequenceEqual(CraftingCatalog(different, second).Fingerprint),
                "changed static recipe metadata must invalidate calibration");
        Expect(!expected.SequenceEqual(CraftingCatalog(second, first).Fingerprint), "recipe index order must affect calibration");
        byte[] returned = catalog.Fingerprint;
        returned[0] ^= 0xff;
        Expect(expected.SequenceEqual(catalog.Fingerprint), "callers cannot mutate the catalog fingerprint");
    }

    private static void Crafting_UsesDetachedAvailableWarehouseCounts()
    {
        using var store = new InventoryStore();
        store.Push(CommandFactory.Put([Stack(1, 5)]));
        store.Pump();
        store.SetEscrowSlots([0], new Dictionary<int, long> { [1] = 2 });
        Expect(store.TryCaptureAvailableTypeCounts(out var snapshot) == CommandStatus.Ok &&
            snapshot.GetValueOrDefault(1) == 3, "locked escrow remainder must be excluded from the snapshot");
        store.SetEscrowSlots([]);
        Expect(store.TryCaptureAvailableTypeCounts(out var current) == CommandStatus.Ok &&
            current.GetValueOrDefault(1) == 5 && snapshot.GetValueOrDefault(1) == 3,
            "releasing escrow must not mutate an earlier snapshot");
    }

    private static void Crafting_RejectsInvalidWireBitmaps()
    {
        Expect(NetworkSystem.ValidRecipeBitmap(9, [0xff, 1]), "a correctly packed bitmap must be accepted");
        Expect(!NetworkSystem.ValidRecipeBitmap(9, [0xff, 2]), "unused high bits must be zero");
        Expect(!NetworkSystem.ValidRecipeBitmap(9, [0xff]), "truncated bitmaps must be rejected");
        Expect(!NetworkSystem.ValidRecipeBitmap(int.MaxValue, []), "out-of-range recipe counts must be rejected");
    }

    private static Definition CraftingRecipe(Ingredient[] inputs, Group[] groups = null) =>
        new(new Ingredient(99, 1), inputs, [], groups ?? [], false, []);

    private static RecipeCatalog CraftingCatalog(params Definition[] definitions) =>
        new(definitions, CraftingItemKey, CraftingTileKey);

    private static string CraftingItemKey(int type) => "Test/Item" + type.ToString(CultureInfo.InvariantCulture);
    private static string CraftingTileKey(int type) => "Test/Tile" + type.ToString(CultureInfo.InvariantCulture);
}
