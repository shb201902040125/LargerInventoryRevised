using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using LargerInventory.Backend.Commands;
using LargerInventory.Backend.Integration;
using LargerInventory.Backend.Network;
using Terraria;
using Terraria.ID;

namespace LargerInventory.Backend.Crafting;

/// <summary>A warehouse-only snapshot of candidate recipes, indexed by the registered RecipeIndex.</summary>
/// <remarks>A set bit is advisory. Check actual instances and reserve materials in a command group for an exact decision.</remarks>
public sealed class CraftingAvailability
{
    private readonly byte[] _bitmap;

    internal CraftingAvailability(int recipeCount, byte[] bitmap)
    {
        if (recipeCount < 0 || bitmap.Length != ((long)recipeCount + 7) / 8)
            throw new ArgumentException("Invalid recipe bitmap length.", nameof(bitmap));
        RecipeCount = recipeCount;
        _bitmap = bitmap;
    }

    public int RecipeCount { get; }

    public bool IsAvailable(int recipeIndex) => (uint)recipeIndex < (uint)RecipeCount &&
        (_bitmap[recipeIndex >> 3] & (1 << (recipeIndex & 7))) != 0;

    /// <summary>Copies the packed bitmap. Recipe zero is the lowest bit in the first byte.</summary>
    public byte[] CopyBitmap() => (byte[])_bitmap.Clone();
}

/// <summary>Queries candidate recipes for the current character's personal warehouse or the world warehouse.</summary>
public static class CraftingQueries
{
    private const int MaxActiveQueries = 2;
    private sealed record QueryJob(Task<byte[]> Work, RecipeCatalog Catalog, InventoryStore Store,
        Action<CommandResult> Callback, CancellationTokenSource Cancellation);
    private static readonly List<QueryJob> Jobs = new();
    private static int _starting;
    private static int _ownerThread;
    internal static RecipeCatalog Catalog { get; private set; }

    /// <summary>Returns a candidate bitmap for one warehouse. Invoke on the game thread.</summary>
    /// <remarks>The default scope is the current character's personal warehouse. In multiplayer the server supplies the result.</remarks>
    public static void QueryAvailableRecipes(Action<CommandResult> callback, bool world = false)
    {
        ArgumentNullException.ThrowIfNull(callback);
        if (Main.netMode == NetmodeID.MultiplayerClient)
        {
            NetworkSystem.RequestAvailableRecipes(world, callback);
            return;
        }
        if (Main.netMode == NetmodeID.Server)
        {
            Reply(callback, CommandResult.Fail(CommandStatus.InvalidArg));
            return;
        }

        SetupCatalog();
        var store = world ? InventorySystem.Instance?.WorldStore :
            Main.LocalPlayer?.GetModPlayer<InventoryPlayer>().Store;
        Query(store, callback, Main.LocalPlayer);
    }

    internal static void SetupCatalog()
    {
        if (_ownerThread != 0) return;
        _ownerThread = Environment.CurrentManagedThreadId;
        try { Catalog = RecipeCatalog.Capture(); }
        catch (Exception error) { Log.Error("Could not build the warehouse recipe catalog.", error); }
    }

    internal static void Query(InventoryStore store, Action<CommandResult> callback, Player player = null)
    {
        ArgumentNullException.ThrowIfNull(callback);
        if (_ownerThread == 0 || _ownerThread != Environment.CurrentManagedThreadId)
            throw new InvalidOperationException("Warehouse crafting queries must start on the game thread.");
        if (Catalog is not { } catalog || store is null)
        {
            Reply(callback, CommandResult.Fail(CommandStatus.Preparing));
            return;
        }
        if (Jobs.Count + _starting >= MaxActiveQueries)
        {
            Reply(callback, CommandResult.Fail(CommandStatus.Throttled));
            return;
        }
        var status = store.TryCaptureAvailableTypeCounts(out var counts);
        if (status != CommandStatus.Ok)
        {
            Reply(callback, CommandResult.Fail(status));
            return;
        }

        _starting++;
        try
        {
            var conditions = catalog.CaptureConditions(player);
            var cancellation = new CancellationTokenSource();
            var token = cancellation.Token;
            Jobs.Add(new QueryJob(Task.Run(() => catalog.Evaluate(counts, conditions, token), token),
                catalog, store, callback, cancellation));
        }
        catch (Exception error)
        {
            Log.Error("Could not start a warehouse crafting query.", error);
            Reply(callback, CommandResult.Fail(CommandStatus.InternalError));
        }
        finally { _starting--; }
    }

    internal static void Pump()
    {
        if (Jobs.Count == 0) return;

        // Snapshot completed jobs before callbacks, which may start another query or close the world.
        var completed = Jobs.FindAll(static job => job.Work.IsCompleted);
        foreach (var job in completed)
        {
            if (!Jobs.Remove(job)) continue;
            job.Cancellation.Dispose();
            if (job.Work.IsCanceled || job.Store.IsDisposed || job.Catalog != Catalog)
                Reply(job.Callback, CommandResult.Fail(CommandStatus.Cancelled));
            else if (job.Work.IsCompletedSuccessfully)
                Reply(job.Callback, new CommandResult(CommandStatus.Ok,
                    new CraftingAvailability(job.Catalog.Count, job.Work.Result)));
            else
            {
                Log.Error("Warehouse crafting query failed.", job.Work.Exception);
                Reply(job.Callback, CommandResult.Fail(CommandStatus.InternalError));
            }
        }
    }

    internal static void CancelQueries()
    {
        var jobs = Jobs.ToArray();
        Jobs.Clear();
        foreach (var job in jobs)
        {
            job.Cancellation.Cancel();
            _ = job.Work.ContinueWith(work =>
            {
                _ = work.Exception;
                job.Cancellation.Dispose();
            }, TaskScheduler.Default);
            Reply(job.Callback, CommandResult.Fail(CommandStatus.Cancelled));
        }
    }

    internal static void Unload()
    {
        Catalog = null;
        CancelQueries();
        _ownerThread = 0;
    }

    private static void Reply(Action<CommandResult> callback, CommandResult result)
    {
        try { callback(result); }
        catch (Exception error) { Log.Error("Warehouse crafting query callback failed.", error); }
    }
}
