using System.Linq;
using LargerInventory.Backend.Storage;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;
using Terraria.ModLoader.IO;

namespace LargerInventory.Backend.Integration;

/// <summary>
/// 玩家作用域 Store：跟随本地/服务器端玩家存档的 .LI 文件夹。
/// 客户端多人模式不持有 Store（由服务器保存，客户端只请求结果）。
/// </summary>
public sealed class InventoryPlayer : ModPlayer
{
    public InventoryStore Store { get; private set; }

    private bool _inMemory;

    public override void LoadData(TagCompound tag)
    {
        if (Main.netMode == NetmodeID.MultiplayerClient)
        {
            // v0.5：联机客户端不持有实例，只向服务器请求结果。
            return;
        }

        CreateStore();
    }

    public override void SaveData(TagCompound tag)
    {
        if (Main.netMode == NetmodeID.MultiplayerClient)
        {
            return;
        }

        if (Store is null)
        {
            return;
        }

        if (!Store.FlushForSave(out var error))
        {
            Log.Error($"Player store save failed: {error}");
        }
    }

    public override void OnEnterWorld()
    {
        if (Main.netMode == NetmodeID.MultiplayerClient)
        {
            return;
        }

        if (Store is null || _inMemory)
        {
            CreateStore();
        }
    }

    public override void PlayerDisconnect()
    {
        DisposeStore();
    }

    private void CreateStore()
    {
        DisposeStore();

        var path = ResolvePlayerFilePath();
        if (string.IsNullOrWhiteSpace(path))
        {
            Log.Warn("Player store path not resolved; using in-memory store for this session.");
            _inMemory = true;
            Store = new InventoryStore(new StoreConfig());
            Store.Start();
            return;
        }

        _inMemory = false;
        var directory = StoreLocator.GetPlayerStoreDirectory(path);
        Store = new InventoryStore(new StoreConfig(), directory, StoreScope.Player);
        Store.Start();
        Log.Info($"Player store opened: {directory}");
    }

    private string ResolvePlayerFilePath()
    {
        var path = Main.GetPlayerPathFromName(Player.name, false);
        if (!string.IsNullOrWhiteSpace(path))
        {
            return path;
        }

        var data = Main.PlayerList.FirstOrDefault(fd => fd.Player == Player);
        if (!string.IsNullOrWhiteSpace(data?.Path))
        {
            return data.Path;
        }

        return Main.ActivePlayerFileData?.Path;
    }

    private void DisposeStore()
    {
        if (Store is null)
        {
            return;
        }

        Store.Dispose();
        Store = null;
    }
}


