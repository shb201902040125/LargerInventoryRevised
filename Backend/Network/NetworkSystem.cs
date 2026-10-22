using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using LargerInventory.Backend;
using LargerInventory.Backend.Commands;
using LargerInventory.Backend.Integration;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;
using Terraria.ModLoader.IO;

namespace LargerInventory.Backend.Network;

/// <summary>
/// 极简请求/结果网络层：
///   客户端发送 CommandRequest → 服务器在该玩家的 Store 上执行 → 回 CommandResult。
/// 只同步结果，不同步实例/页；ESCAPE 不走此路径（由第三方自行同步）。
/// </summary>
public static class NetworkSystem
{
    private const byte ScopePlayer = 0;
    private const byte ScopeWorld = 1;
    private const byte OpRequest = 1;
    private const byte OpResult = 2;

    private static readonly ConcurrentDictionary<int, Action<CommandResult>> Pending = new();
    private static int _nextSequence;

    public static void RequestPlayerCommand(Command command, Action<CommandResult> callback)
    {
        RequestCommand(command, callback, ScopePlayer);
    }

    public static void RequestWorldCommand(Command command, Action<CommandResult> callback)
    {
        RequestCommand(command, callback, ScopeWorld);
    }

    private static void RequestCommand(Command command, Action<CommandResult> callback, byte scope)
    {
        if (Main.netMode != NetmodeID.MultiplayerClient)
        {
            throw new InvalidOperationException("NetworkSystem.RequestPlayerCommand is for multiplayer clients only.");
        }

        if (command is null)
        {
            throw new ArgumentNullException(nameof(command));
        }

        var sequence = Interlocked.Increment(ref _nextSequence);
        if (callback is not null)
        {
            Pending[sequence] = callback;
        }

        var packet = LargerInventory.Instance.GetPacket();
        packet.Write(OpRequest);
        packet.Write(scope);
        packet.Write(sequence);
        WriteTag(packet, CommandSerializer.Save(command));
        packet.Send();
    }

    public static bool HandlePacket(BinaryReader reader, int whoAmI)
    {
        var opcode = reader.ReadByte();
        switch (opcode)
        {
            case OpRequest when Main.netMode == NetmodeID.Server:
                HandleRequest(reader, whoAmI);
                return true;

            case OpResult when Main.netMode == NetmodeID.MultiplayerClient:
                HandleResult(reader);
                return true;

            default:
                return false;
        }
    }

    private static void HandleRequest(BinaryReader reader, int whoAmI)
    {
        var scope = reader.ReadByte();
        var sequence = reader.ReadInt32();
        var tag = ReadTag(reader);
        if (tag is null)
        {
            return;
        }

        Command command;
        try
        {
            command = CommandSerializer.Load(tag);
        }
        catch (Exception e)
        {
            Log.Error("Failed to deserialize command request.", e);
            return;
        }

        var store = scope == ScopeWorld
            ? InventorySystem.Instance?.WorldStore
            : (whoAmI >= 0 && whoAmI < Main.maxPlayers
                ? Main.player[whoAmI].GetModPlayer<InventoryPlayer>()?.Store
                : null);

        if (store is null)
        {
            SendResult(whoAmI, sequence, CommandResult.Fail(CommandStatus.InternalError));
            return;
        }

        var capturedSequence = sequence;
        var capturedPlayer = whoAmI;
        command = CommandSerializer.Load(tag, result =>
        {
            // 执行线程回调：投递到主线程再发包。
            InventorySystem.EnqueueMainThreadAction(() => SendResult(capturedPlayer, capturedSequence, result));
        });
        store.Push(command);
    }

    private static void HandleResult(BinaryReader reader)
    {
        var scope = reader.ReadByte();
        var sequence = reader.ReadInt32();
        var tag = ReadTag(reader);
        if (tag is null || !Pending.TryRemove(sequence, out var callback))
        {
            return;
        }

        if (!CommandResultSerializer.TryLoad(tag, out var result))
        {
            result = CommandResult.Fail(CommandStatus.InternalError);
        }

        try
        {
            callback?.Invoke(result);
        }
        catch (Exception e)
        {
            Log.Error("Network result callback failed.", e);
        }
    }

    private static void SendResult(int toClient, int sequence, CommandResult result)
    {
        var packet = LargerInventory.Instance.GetPacket();
        packet.Write(OpResult);
        packet.Write(sequence);
        WriteTag(packet, CommandResultSerializer.Save(result));
        packet.Send(toClient, -1);
    }

    private static void WriteTag(ModPacket packet, TagCompound tag)
    {
        using var ms = new MemoryStream();
        TagIO.ToStream(tag, ms, compress: false);
        var bytes = ms.ToArray();
        packet.Write(bytes.Length);
        packet.Write(bytes);
    }

    private static TagCompound ReadTag(BinaryReader reader)
    {
        var length = reader.ReadInt32();
        if (length <= 0 || length > 16 * 1024 * 1024)
        {
            return null;
        }

        var bytes = reader.ReadBytes(length);
        using var ms = new MemoryStream(bytes);
        return TagIO.FromStream(ms, false);
    }
}


