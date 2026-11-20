using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using LargerInventory.Backend.Commands;
using LargerInventory.Backend.Network;
using LargerInventory.Backend.Storage;
using Terraria;
using Terraria.ID;
using Terraria.IO;
using Terraria.ModLoader;
using Terraria.ModLoader.IO;
using Terraria.Utilities;

namespace LargerInventory.Backend.Integration;

public sealed class InventorySystem : ModSystem
{
    public static InventorySystem Instance { get; private set; }

    private static volatile bool _commandsPaused;
    public static bool CommandsPaused => _commandsPaused || Instance?._leavingSinglePlayer == true;

    public Guid WorldId { get; private set; }
    public InventoryStore WorldStore { get; private set; }

    internal StoreConfig Config => StoreConfig.Runtime;

    private readonly object _storesGate = new();
    private readonly ConcurrentQueue<Action> _mainThreadActions = new();
    private readonly Dictionary<Guid, InventoryStore> _serverPlayerStores = new();
    private readonly Dictionary<Guid, long> _idlePlayerStoreDeadlines = new();
    private readonly Dictionary<string, InventoryStore> _closingStores = new(OperatingSystem.IsWindows() ?
        StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private volatile bool _leavingSinglePlayer;
    private long _nextCloseRetry;
    private long _nextIdlePlayerStoreCheck;
    private readonly Dictionary<int, (InventoryPlayer Player, Guid Id)> _serverBindings = new();
    private InventoryPlayer _localPlayer;
    private readonly ConditionalWeakTable<InventoryStore, PeriodicSaveDeadline> _serverSaveDeadlines = new();

    private sealed class PeriodicSaveDeadline(long next)
    {
        internal long Next = next;
    }

    public static void EnqueueMainThreadAction(Action action)
    {
        if (action is not null)
        {
            Instance?._mainThreadActions.Enqueue(action);
        }
    }

    public static void PauseAllCommands()
    {
        var system = Instance;
        if (system is not null && Main.netMode != NetmodeID.SinglePlayer)
        {
            return;
        }

        _commandsPaused = true;
        if (system is not null)
        {
            foreach (var store in system.GetOpenStores())
            {
                store.AbortCommands(CommandStatus.ManualInterrupt);
            }
        }
    }

    public static void ResumeAllCommands()
    {
        if (Instance is null || Main.netMode == NetmodeID.SinglePlayer)
        {
            _commandsPaused = false;
        }
    }

    public override void OnModLoad()
    {
        Config.Normalize();
        Instance = this;
        On_Player.SavePlayer += OnSavePlayer;
        On_WorldGen.SaveAndQuit += OnSaveAndQuit;
        On_Main.ErasePlayer += OnErasePlayer;
        On_Main.EraseWorld += OnEraseWorld;
    }

    public override void OnModUnload()
    {
        On_Player.SavePlayer -= OnSavePlayer;
        On_WorldGen.SaveAndQuit -= OnSaveAndQuit;
        On_Main.ErasePlayer -= OnErasePlayer;
        On_Main.EraseWorld -= OnEraseWorld;
        CloseWorld();
        Instance = null;
    }

    public override void PostWorldLoad()
    {
        if (Main.netMode == NetmodeID.MultiplayerClient)
        {
            return;
        }

        var file = Main.ActiveWorldFileData;
        lock (_storesGate)
        {
            if (WorldStore is not null)
            {
                return;
            }

            try
            {
                WorldId = file?.UniqueId ?? Guid.Empty;
                var savedPath = file?.Path;
                if (string.IsNullOrWhiteSpace(savedPath))
                {
                    savedPath = Main.worldPathName;
                }

                var directory = StoreIdentity.OpenDirectory(savedPath, file?.IsCloudSave == true, StoreScope.World, WorldId, EnsureClosed);
                WorldStore = OpenStore(directory, StoreScope.World);
            }
            catch (Exception e)
            {
                Log.Error("Could not open the world store.", e);
            }
        }
    }

    internal void OpenLocalPlayer(InventoryPlayer player, PlayerFileData file)
    {
        lock (_storesGate)
        {
            if (_localPlayer == player && player.Store is not null)
            {
                return;
            }

            if (_localPlayer is not null)
            {
                throw new InvalidOperationException("A different local character store is already open.");
            }

            var directory = StoreIdentity.OpenDirectory(file.Path, file.IsCloudSave, StoreScope.Player, player.PersistentPlayerId, EnsureClosed);
            player.Store = OpenStore(directory, StoreScope.Player);
            _localPlayer = player;
        }
    }

    // Network handler invokes this on the game thread after receiving the character UUID.
    internal bool TryBindServerPlayer(int whoAmI, Guid playerId, out InventoryStore store)
    {
        store = null;
        if (Main.netMode != NetmodeID.Server || playerId == Guid.Empty || (uint)whoAmI >= Main.maxPlayers ||
            Main.player[whoAmI] is not { active: true } player || !player.TryGetModPlayer<InventoryPlayer>(out var modPlayer))
        {
            return false;
        }

        lock (_storesGate)
        {
            if (WorldId == Guid.Empty || WorldStore?.DirectoryPath is not { } worldDirectory)
            {
                return false;
            }

            if (_serverBindings.TryGetValue(whoAmI, out var binding))
            {
                if (binding.Player == modPlayer && binding.Id != playerId)
                {
                    return false;
                }

                if (binding.Player != modPlayer)
                {
                    // A newly created Terraria Player has taken the slot before the old hook ran.
                    RemoveServerBinding(whoAmI, binding);
                }
            }

            if (!_serverPlayerStores.TryGetValue(playerId, out store))
            {
                try
                {
                    var path = StoreIdentity.ServerPlayerDirectory(worldDirectory, playerId);
                    store = OpenStore(path, StoreScope.Player);
                    _serverPlayerStores.Add(playerId, store);
                }
                catch (Exception e)
                {
                    Log.Error($"Could not open server character store {playerId:N} in {WorldId:N}.", e);
                    return false;
                }
            }

            _serverBindings[whoAmI] = (modPlayer, playerId);
            _idlePlayerStoreDeadlines.Remove(playerId);
            modPlayer.Store = store;
            return true;
        }
    }

    internal void UnbindServerPlayer(InventoryPlayer player)
    {
        lock (_storesGate)
        {
            var slot = player.Player.whoAmI;
            if (_serverBindings.TryGetValue(slot, out var binding) && binding.Player == player)
            {
                RemoveServerBinding(slot, binding);
            }
        }
    }

    private void RemoveServerBinding(int slot, (InventoryPlayer Player, Guid Id) binding)
    {
        _serverBindings.Remove(slot);
        binding.Player.Store = null;
        if (!_serverBindings.Values.Any(other => other.Id == binding.Id))
        {
            _idlePlayerStoreDeadlines[binding.Id] = Environment.TickCount64 + Config.PlayerStoreIdleMs;
        }
    }

    private void OnSaveAndQuit(On_WorldGen.orig_SaveAndQuit orig, Action callback)
    {
        if (Main.netMode != NetmodeID.SinglePlayer)
        {
            orig(callback);
            return;
        }

        if (!Program.IsMainThread)
        {
            Main.QueueMainThreadAction(() => BeginSinglePlayerExit(orig, callback));
            return;
        }

        BeginSinglePlayerExit(orig, callback);
    }

    private void BeginSinglePlayerExit(On_WorldGen.orig_SaveAndQuit orig, Action callback)
    {
        _leavingSinglePlayer = true;
        try
        {
            // Cancel queued work and settle its returned items on the game thread before vanilla saves.
            foreach (var store in GetOpenStores())
            {
                store.AbortCommands(CommandStatus.ManualInterrupt);
            }

            DrainMainThreadActions();
            PlayerItemDelivery.RetrySinglePlayer(Main.LocalPlayer);
            orig(callback);
        }
        catch
        {
            _leavingSinglePlayer = false;
            throw;
        }
    }

    private void OnSavePlayer(On_Player.orig_SavePlayer orig, PlayerFileData file, bool skipMapSave)
    {
        // Save-and-quit invokes this hook on a worker. Only commit snapshots prepared by Pump.
        if (Main.netMode == NetmodeID.SinglePlayer)
        {
            InventoryStore store;
            lock (_storesGate)
            {
                store = _localPlayer is not null && _localPlayer.Player == file?.Player ? _localPlayer.Store : null;
            }

            FlushStore(store);
        }

        orig(file, skipMapSave);
    }

    // tModLoader requires SaveWorldData and LoadWorldData to be overridden together.
    // Warehouse state is restored from its own files in PostWorldLoad.
    public override void LoadWorldData(TagCompound tag) { }

    public override void SaveWorldData(TagCompound tag)
    {
        if (Main.netMode != NetmodeID.MultiplayerClient)
        {
            foreach (var store in GetOpenStores(includeLocalPlayer: false))
            {
                FlushStore(store);
            }
        }
    }

    internal static void FlushStore(InventoryStore store)
    {
        if (store is not null && !store.FlushForSave(out var error))
        {
            Log.Error($"Store save failed ({store.DirectoryPath}): {error}");
        }
    }

    public override void OnWorldUnload() => CloseWorld();

    public override void PostUpdateEverything()
    {
        if (_leavingSinglePlayer)
        {
            return;
        }

        if (Environment.TickCount64 >= _nextCloseRetry)
        {
            _nextCloseRetry = Environment.TickCount64 + Config.StoreCloseRetryMs;
            RetryClosingStores();
        }

        var stores = GetOpenStores();
        foreach (var store in stores)
        {
            try
            {
                store.Pump();
            }
            catch (Exception e)
            {
                Log.Error($"Store pump failed ({store.DirectoryPath}).", e);
            }
        }

        NetworkSystem.Update();
        DrainMainThreadActions();
        if (Main.netMode == NetmodeID.SinglePlayer)
        {
            PlayerItemDelivery.RetrySinglePlayer(Main.LocalPlayer);
        }
        else if (Main.netMode == NetmodeID.Server)
        {
            var now = Environment.TickCount64;
            foreach (var store in stores)
            {
                if (!_serverSaveDeadlines.TryGetValue(store, out var deadline))
                {
                    var stagger = Config.ServerSaveStaggerMs;
                    var offset = stagger == 0 ? 0 : (int)(unchecked((uint)RuntimeHelpers.GetHashCode(store)) % (uint)stagger);
                    deadline = new PeriodicSaveDeadline(now + Config.ServerSaveIntervalMs - stagger + offset);
                    _serverSaveDeadlines.Add(store, deadline);
                }
                if (now < deadline.Next)
                    continue;

                deadline.Next = now + Config.ServerSaveIntervalMs;
                store.SchedulePeriodicSave();
            }

            if (now >= _nextIdlePlayerStoreCheck)
            {
                _nextIdlePlayerStoreCheck = now + Config.PlayerStoreCheckIntervalMs;
                CloseIdlePlayerStores(now);
            }
        }
    }

    private void CloseIdlePlayerStores(long now)
    {
        lock (_storesGate)
        {
            foreach (var playerId in _idlePlayerStoreDeadlines.Where(entry => now >= entry.Value).Select(entry => entry.Key).ToArray())
            {
                var store = _serverPlayerStores[playerId];
                if (store.IsBusy || store.Queue.Count > 0 || !NetworkSystem.CanClosePlayerStore(store))
                {
                    continue;
                }

                // Hold the gate through disposal so a reconnect cannot bind a store mid-close.
                if (!TryDisposeStore(store))
                {
                    // Keep the same instance available for pumping, reconnects, and retries.
                    _idlePlayerStoreDeadlines[playerId] = Environment.TickCount64 + Config.PlayerStoreCloseRetryMs;
                    continue;
                }

                _idlePlayerStoreDeadlines.Remove(playerId);
                _serverPlayerStores.Remove(playerId);
            }
        }
    }

    private InventoryStore[] GetOpenStores(bool includeLocalPlayer = true)
    {
        lock (_storesGate)
        {
            var stores = new List<InventoryStore>(_serverPlayerStores.Values);
            if (WorldStore is not null)
            {
                stores.Add(WorldStore);
            }

            if (includeLocalPlayer && _localPlayer?.Store is { } localStore)
            {
                stores.Add(localStore);
            }

            return stores.ToArray();
        }
    }

    private InventoryStore OpenStore(string directory, StoreScope scope)
    {
        EnsureClosed(directory);
        var store = new InventoryStore(Config.Copy(), directory, scope);
        try
        {
            store.Start();
            Log.Info($"{scope} store opened: {directory}");
            return store;
        }
        catch
        {
            store.Dispose();
            throw;
        }
    }

    private void EnsureClosed(string directory)
    {
        directory = Path.GetFullPath(directory);
        lock (_storesGate)
        {
            foreach (var path in _closingStores.Keys.Where(path => IsWithinDirectory(path, directory)).ToArray())
            {
                if (!TryCloseStore(path))
                {
                    throw new IOException($"The previous store still has unsaved changes: {path}");
                }
            }
        }
    }

    private static bool IsWithinDirectory(string path, string directory) =>
        StoreIdentity.PathsEqual(path, directory) || path.StartsWith(directory + Path.DirectorySeparatorChar,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private bool TryCloseStore(string path)
    {
        if (!TryDisposeStore(_closingStores[path]))
        {
            return false;
        }

        _closingStores.Remove(path);
        return true;
    }

    private static bool TryDisposeStore(InventoryStore store)
    {
        if (store.TryDispose(out var error))
        {
            return true;
        }

        Log.Error($"Store close failed and will be retried ({store.DirectoryPath}): {error}");
        return false;
    }

    private void RetryClosingStores()
    {
        lock (_storesGate)
        {
            foreach (var path in _closingStores.Keys.ToArray())
            {
                TryCloseStore(path);
            }
        }
    }

    private void CloseWorld()
    {
        NetworkSystem.CancelAll();
        lock (_storesGate)
        {
            foreach (var store in GetOpenStores())
            {
                _closingStores.Add(Path.GetFullPath(store.DirectoryPath), store);
            }

            WorldStore = null;
            WorldId = Guid.Empty;
            if (_localPlayer is not null)
            {
                _localPlayer.Store = null;
                _localPlayer = null;
            }

            foreach (var binding in _serverBindings.Values)
            {
                binding.Player.Store = null;
            }

            _serverBindings.Clear();
            _idlePlayerStoreDeadlines.Clear();
            _serverPlayerStores.Clear();
        }

        RetryClosingStores();
        DrainMainThreadActions();
        InventoryGlobalItem.Clear();
        _mainThreadActions.Clear();
        _commandsPaused = false;
        _leavingSinglePlayer = false;
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

    private void OnErasePlayer(On_Main.orig_ErasePlayer orig, int index)
    {
        var file = (uint)index < Main.PlayerList.Count ? Main.PlayerList[index] : null;
        // Capture the UUID from the saved .tplr before the original game call erases it.
        EraseSave(() => orig(index), file, StoreScope.Player,
            () => StoreIdentity.ReadSavedPlayerId(file.Path, file.IsCloudSave, Mod.Name));
    }

    private void OnEraseWorld(On_Main.orig_EraseWorld orig, int index)
    {
        var file = (uint)index < Main.WorldList.Count ? Main.WorldList[index] : null;
        EraseSave(() => orig(index), file, StoreScope.World, () => file.UniqueId);
    }

    private void EraseSave(Action erase, FileData file, StoreScope scope, Func<Guid> readId)
    {
        string storeDirectory = null;
        string path = null;
        string modPath = null;
        var cloud = file?.IsCloudSave == true;
        try
        {
            if (!string.IsNullOrWhiteSpace(file?.Path))
            {
                path = cloud ? file.Path : Path.GetFullPath(file.Path);
                modPath = Path.ChangeExtension(path, scope == StoreScope.Player ? ".tplr" : ".twld");
                if (!SaveMissing(path, cloud))
                {
                    var id = readId();
                    storeDirectory = cloud ? StoreIdentity.FindCloudDirectory(scope, id) :
                        StoreIdentity.FindLocalDirectory(path, scope, id);
                }
            }
        }
        catch (Exception e)
        {
            Log.Error($"Could not verify the {scope} store association before deletion.", e);
        }

        erase();

        if (storeDirectory is null)
        {
            return;
        }

        try
        {
            if (!SaveMissing(path, cloud) || !SaveMissing(modPath, cloud))
            {
                Log.Warn($"Game did not remove {path} and its mod data. Store retained: {storeDirectory}");
                return;
            }

            lock (_storesGate)
            {
                if (GetOpenStores().Any(store => IsWithinDirectory(store.DirectoryPath, storeDirectory)) ||
                    _closingStores.Keys.Any(path => IsWithinDirectory(path, storeDirectory)))
                {
                    Log.Warn($"Store is still open or has unsaved changes and was retained: {storeDirectory}");
                    return;
                }

                StoreIdentity.RecycleDirectory(storeDirectory);
            }
            Log.Info($"Moved {scope} store to system trash: {storeDirectory}");
        }
        catch (Exception e)
        {
            Log.Error($"Could not move the {scope} store to system trash: {storeDirectory}", e);
        }
    }

    private static bool SaveMissing(string path, bool cloud) => cloud
        ? Terraria.Social.SocialAPI.Cloud is not null && !FileUtilities.Exists(path, true)
        : StoreIdentity.IsMissing(path);
}
