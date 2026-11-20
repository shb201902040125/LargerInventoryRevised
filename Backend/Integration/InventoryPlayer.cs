using System;
using System.Collections.Generic;
using LargerInventory.Backend.Network;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;
using Terraria.ModLoader.IO;

namespace LargerInventory.Backend.Integration;

public sealed class InventoryPlayer : ModPlayer
{
    // Remote server slots borrow a shared store. InventorySystem owns all open stores.
    public InventoryStore Store { get; internal set; }
    public Guid PersistentPlayerId { get; private set; }
    public Dictionary<string, byte[]> DeliveryReceipts { get; private set; }

    public override void Initialize()
    {
        DeliveryReceipts = new Dictionary<string, byte[]>();
    }

    public override void LoadData(TagCompound tag)
    {
        PersistentPlayerId = StoreIdentity.ReadPlayerId(tag);
        InventoryClientState.LoadPlayerReceipts(tag, DeliveryReceipts);
        PlayerItemDelivery.LoadPending(Player, tag);
    }

    public override void SaveData(TagCompound tag)
    {
        tag[StoreIdentity.PlayerIdKey] = EnsureCharacterId().ToString("N");
        InventoryClientState.SavePlayerReceipts(tag, DeliveryReceipts);
        PlayerItemDelivery.SavePending(Player, tag);
    }

    public override void OnEnterWorld()
    {
        if (Main.netMode == NetmodeID.Server || Player.whoAmI != Main.myPlayer)
        {
            return;
        }

        var file = Main.ActivePlayerFileData;
        try
        {
            if (file?.Player != Player || string.IsNullOrWhiteSpace(file.Path))
            {
                throw new InvalidOperationException("The selected character save has no usable path.");
            }

            if (PersistentPlayerId == Guid.Empty)
            {
                // Upgrade an existing character before exposing its ID to a server or marking a local store.
                var assigned = EnsureCharacterId();
                Player.SavePlayer(file, skipMapSave: true);
                if (StoreIdentity.ReadSavedPlayerId(file.Path, file.IsCloudSave, Mod.Name) != assigned)
                {
                    throw new InvalidOperationException("The game did not persist the character UUID.");
                }
            }
            else if (StoreIdentity.ReadSavedPlayerId(file.Path, file.IsCloudSave, Mod.Name) != PersistentPlayerId)
            {
                throw new InvalidOperationException("The character UUID does not match the selected save.");
            }

            if (Main.netMode == NetmodeID.MultiplayerClient)
            {
                NetworkSystem.SendHello(PersistentPlayerId);
            }
            else
            {
                InventorySystem.Instance?.OpenLocalPlayer(this, file);
            }
        }
        catch (Exception e)
        {
            Log.Error("Could not initialize the Larger Inventory character identity.", e);
        }
    }

    public override void PlayerDisconnect()
    {
        NetworkSystem.ForgetPlayer(Player.whoAmI);
        InventorySystem.Instance?.UnbindServerPlayer(this);
    }

    private Guid EnsureCharacterId()
    {
        if (PersistentPlayerId == Guid.Empty)
        {
            PersistentPlayerId = Guid.NewGuid();
        }

        return PersistentPlayerId;
    }
}
