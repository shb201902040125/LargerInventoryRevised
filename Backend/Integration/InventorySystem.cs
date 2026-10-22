using System;
using System.Collections.Concurrent;
using System.IO;
using LargerInventory.Backend;
using LargerInventory.Backend.Storage;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;
using Terraria.ModLoader.IO;

namespace LargerInventory.Backend.Integration;

/// <summary>
/// 模组级系统：世界 Store（共享）+ 主线程后处理队列 + 存档作用域生命周期。
/// 玩家 Store 在 <see cref="InventoryPlayer"/>。
/// </summary>
public sealed class InventorySystem : ModSystem
{
    public static InventorySystem Instance { get; private set; }

    public InventoryStore WorldStore { get; private set; }

    private readonly ConcurrentQueue<Action> _mainThreadActions = new();

    /// <summary>接入层回调线程 → 主线程后处理队列（不是指令层能力）。</summary>
    public static void EnqueueMainThreadAction(Action action)
    {
        if (action is null)
        {
            return;
        }

        Instance?._mainThreadActions.Enqueue(action);
    }

    public InventoryStore GetOrCreateWorldStore()
    {
        if (WorldStore is null)
        {
            ReplaceWorldStore();
        }

        return WorldStore;
    }

    public override void OnModLoad()
    {
        Instance = this;
        On_Main.ErasePlayer += OnErasePlayer;
        On_Main.EraseWorld += OnEraseWorld;
    }

    public override void OnModUnload()
    {
        On_Main.ErasePlayer -= OnErasePlayer;
        On_Main.EraseWorld -= OnEraseWorld;

        DrainMainThreadActions();
        WorldStore?.Dispose();
        WorldStore = null;
        _mainThreadActions.Clear();
        Instance = null;
    }

    public override void OnWorldLoad()
    {
        if (Main.netMode == NetmodeID.MultiplayerClient)
        {
            return;
        }

        ReplaceWorldStore();
    }

    public override void LoadWorldData(TagCompound tag)
    {
        if (Main.netMode == NetmodeID.MultiplayerClient)
        {
            return;
        }

        ReplaceWorldStore();
    }

    public override void SaveWorldData(TagCompound tag)
    {
        if (Main.netMode == NetmodeID.MultiplayerClient || WorldStore is null)
        {
            return;
        }

        if (!WorldStore.FlushForSave(out var error))
        {
            Log.Error($"World store save failed: {error}");
        }
    }

    public override void OnWorldUnload()
    {
        DrainMainThreadActions();
        WorldStore?.Dispose();
        WorldStore = null;
        _mainThreadActions.Clear();
    }

    public override void PostUpdateEverything()
    {
        DrainMainThreadActions();
    }

    private void ReplaceWorldStore()
    {
        WorldStore?.Dispose();
        WorldStore = null;

        var directory = StoreLocator.GetActiveWorldStoreDirectory();
        if (string.IsNullOrWhiteSpace(directory))
        {
            Log.Warn("World store path not resolved; using in-memory store for this session.");
            WorldStore = new InventoryStore(new StoreConfig());
        }
        else
        {
            WorldStore = new InventoryStore(new StoreConfig(), directory, StoreScope.World);
            Log.Info($"World store opened: {directory}");
        }

        WorldStore.Start();
    }

    private void DrainMainThreadActions()
    {
        while (_mainThreadActions.TryDequeue(out var action))
        {
            try
            {
                action();
            }
            catch (Exception e)
            {
                Log.Error("Main-thread post action failed.", e);
            }
        }
    }

    private void OnErasePlayer(On_Main.orig_ErasePlayer orig, int i)
    {
        string path = null;
        try
        {
            if (i >= 0 && i < Main.PlayerList.Count)
            {
                path = Main.PlayerList[i].Path;
            }
        }
        catch (Exception e)
        {
            Log.Error("Failed to resolve player file path before erase.", e);
        }

        orig(i);

        DeleteStoreFolder(StoreLocator.GetPlayerStoreDirectory(path), "player");
    }

    private void OnEraseWorld(On_Main.orig_EraseWorld orig, int i)
    {
        string path = null;
        try
        {
            if (i >= 0 && i < Main.WorldList.Count)
            {
                path = Main.WorldList[i].Path;
            }
        }
        catch (Exception e)
        {
            Log.Error("Failed to resolve world file path before erase.", e);
        }

        orig(i);

        DeleteStoreFolder(StoreLocator.GetWorldStoreDirectory(path), "world");
    }

    private static void DeleteStoreFolder(string directory, string label)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return;
        }

        try
        {
            if (!directory.EndsWith(".LI", StringComparison.OrdinalIgnoreCase))
            {
                Log.Warn($"Refusing to delete unexpected store directory: {directory}");
                return;
            }

            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
                Log.Info($"Deleted {label} store folder: {directory}");
            }
        }
        catch (Exception e)
        {
            Log.Error($"Failed to delete {label} store folder: {directory}", e);
        }
    }
}


