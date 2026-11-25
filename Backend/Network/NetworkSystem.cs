using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using LargerInventory.Backend.Commands;
using LargerInventory.Backend.Integration;
using LargerInventory.Backend.Storage;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;
using Terraria.ModLoader.IO;

namespace LargerInventory.Backend.Network;

/// <summary>Versioned, connection-bound access to authoritative stores and item escrow.</summary>
public static partial class NetworkSystem
{
    public const byte ProtocolVersion = 9;
    private const byte PreparedFlag = 32;
    private const int MaxUploadBytes = 24 * 1024;
    internal const byte GrantFlag = 8;
    internal const byte CompletedOperationFlag = 16;
    internal const int MaxResultBytes = 4 * 1024 * 1024;
    private const int FrameBytes = 24 * 1024; // Below Terraria's ushort packet length limit.
    internal const int MaxWireSlots = 1024; // Maximum slot count accepted by this wire version.
    private const int EnteredConnectionState = 10;

    private static StoreConfig RuntimeConfig => StoreConfig.Runtime;

    internal enum Op : byte { Hello = 1, Welcome, Read, Mutate, Put, DebugFill, Lookup, Receipt, Result, Trusted = 12, Retire, RejectGrant, Consume, PreparePut, ConsumeBatch, AvailableRecipes }
    private enum Scope : byte { Player, World }

    private sealed class Session
    {
        public int Slot;
        public Guid Player;
        public Guid World;
        public Guid Token;
        public Guid JournalLineage;
        public InventoryStore PlayerStore;
        public InventoryStore WorldStore;
        public int Revision;
        public Player ConnectionPlayer;
        public Crafting.RecipeCatalog JoinedRecipeCatalog;
        public Crafting.RecipeCatalog CalibratedRecipes;
        public long NextRecipeQuery;
        public long NextRecipeThrottleReply;
        public readonly Dictionary<(Scope Scope, Guid Request), Source> Sources = new();
        public readonly Dictionary<(Scope Scope, Guid Request), byte[]> PutUploads = new();
        public readonly Queue<QueuedConfirmation> Confirmations = new();
        public bool CommittingTake;
        public readonly HashSet<(InventoryStore Store, Guid Request)> OfferedDeliveries = new();
        public readonly Dictionary<(InventoryStore Store, Guid Request), Guid> SentDeliveries = new();
    }

    private sealed record Source(Func<Command> Reserve, Action<CommandResult> Settle);
    private sealed record Inbound(int Slot, int Revision, Player ConnectionPlayer, Action Run);

    private sealed class Pending
    {
        public Scope Scope;
        public Op Operation;
        public Guid Request;
        public long Sequence;
        public long Started;
        public Action<CommandResult> Callback;
        public Action<ModPacket> Write;
        public bool Sent;
        public byte[] Bytes;
        public int Offset;
        public byte Flags;
    }

    private sealed class Transfer
    {
        public Session Session;
        public Scope Scope;
        public Guid Correlation;
        public Guid Request;
        public byte Flags;
        public byte[] Bytes;
        public int Offset;
    }

    // Packet decoding can run on the socket thread. All world, Item and transaction work runs in Update.
    private static readonly object InboundGate = new();
    private static readonly Queue<Inbound> Incoming = new();
    private static readonly Dictionary<int, int> QueuedBySlot = new();
    private static readonly Dictionary<int, int> SlotRevisions = new();
    private static int _epoch;
    private static readonly Dictionary<int, Session> Sessions = new();
    private static readonly Dictionary<Guid, Pending> Requests = new();
    private static readonly Queue<Transfer> Outgoing = new();
    private static readonly Dictionary<(InventoryStore Store, TransactionLedger.Key Key), Session> DeliveryOwners = new();
    private static readonly Dictionary<(InventoryStore Store, TransactionLedger.Key Key), ServerDelivery> PendingDropSaves = new();
    private static ConditionalWeakTable<InventoryStore, TransactionLedger> _ledgers = new();
    private static Session _client;
    private static Guid _clientPlayer;
    private static long _helloSent;
    private static bool _transactionRunning;

    public static void SendHello(Guid playerId)
    {
        if (Main.netMode != NetmodeID.MultiplayerClient || playerId == Guid.Empty)
        {
            return;
        }

        _clientPlayer = playerId;
        // The game's first update finalizes the local recipe catalog before sending Hello.
        _helloSent = Environment.TickCount64 - RuntimeConfig.NetworkHelloRetryMs;
    }

    private static void WriteHello()
    {
        if (!_sentRecipeHello)
        {
            _helloRecipeCatalog = Crafting.CraftingQueries.Catalog;
            _sentRecipeHello = true;
        }
        var packet = NewPacket(Op.Hello);
        WriteGuid(packet, _clientPlayer);
        WriteRecipeTable(packet, _helloRecipeCatalog);
        packet.Send();
        _helloSent = Environment.TickCount64;
    }

    public static void RequestPlayerCommand(Command command, Action<CommandResult> callback) =>
        RequestCommand(Scope.Player, command, callback, default);

    public static void RequestWorldCommand(Command command, Action<CommandResult> callback) =>
        RequestCommand(Scope.World, command, callback, default);

    public static Guid RequestWorldTransaction(Command command, Action<CommandResult> callback, Guid requestId = default) =>
        RequestCommand(Scope.World, command, callback, requestId);

    public static Guid RequestWorldPut(Guid requestId, Action<CommandResult> callback)
    {
        if (requestId == Guid.Empty)
        {
            throw new ArgumentException("A server-authorized operation ID is required.", nameof(requestId));
        }

        return StartRequest(Scope.World, Op.Put, requestId, callback);
    }

    public static Guid RequestDebugFill(int type, int count, Action<CommandResult> callback, Guid requestId = default) =>
        StartRequest(Scope.World, Op.DebugFill, requestId, callback, packet =>
        {
            packet.Write(type);
            packet.Write(count);
        });

    public static Guid RequestPlayerPut(Guid requestId, Action<CommandResult> callback)
    {
        if (requestId == Guid.Empty)
        {
            throw new ArgumentException("A server-authorized operation ID is required.", nameof(requestId));
        }

        return StartRequest(Scope.Player, Op.Put, requestId, callback);
    }

    public static void RequestWorldResult(Guid requestId, Action<CommandResult> callback) =>
        RequestResult(Scope.World, requestId, callback);

    public static void RequestPlayerResult(Guid requestId, Action<CommandResult> callback) =>
        RequestResult(Scope.Player, requestId, callback);

    private static void RequestResult(Scope scope, Guid requestId, Action<CommandResult> callback)
    {
        var sequence = _clientJournal is not null && _clientJournal.TryGet(requestId, out var entry) &&
            entry.Scope == JournalScope(scope) ? entry.Sequence : 0;
        StartRequest(scope, Op.Lookup, requestId, callback, sequence: sequence);
    }

    private static string ReceiptKey(Session session, Scope scope, Guid request) => $"{session.World:N}/{(byte)scope}/{request:N}";

    internal static Guid RequestRecoverableWorldTransaction(Command command, Action<CommandResult> callback) =>
        RequestCommand(Scope.World, command, callback, default, recoverable: true);

    internal static Guid RequestRecoverableDebugFill(int type, int count, Action<CommandResult> callback) =>
        StartRequest(Scope.World, Op.DebugFill, default, callback, packet =>
        {
            packet.Write(type);
            packet.Write(count);
        }, recoverable: true);

    private static Guid RequestCommand(Scope scope, Command command, Action<CommandResult> callback, Guid requestId,
        bool recoverable = false)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.Kind == CommandKind.Put)
        {
            Invoke(callback ?? command.Callback, Command.FailUnexecuted(command, CommandStatus.InvalidArg));
            return requestId;
        }

        if (command.Kind == CommandKind.Take)
            return PrepareTake(scope, command, callback, requestId, autoConsume: true, reportPreview: false);

        var op = command.Kind is CommandKind.QuerySlot or CommandKind.QueryType ? Op.Read : Op.Mutate;
        // Freeze the small scalar request before a handshake or retry can defer sending it.
        using var bytes = new MemoryStream();
        using var writer = new BinaryWriter(bytes);
        if (!WriteCommand(writer, command, RuntimeConfig))
        {
            Invoke(callback ?? command.Callback, CommandResult.Fail(CommandStatus.InvalidArg));
            return requestId;
        }

        var request = bytes.ToArray();
        return StartRequest(scope, op, requestId, callback ?? command.Callback, packet => packet.Write(request),
            recoverable: recoverable && op == Op.Mutate);
    }

    private static Guid StartRequest(Scope scope, Op operation, Guid request, Action<CommandResult> callback,
        Action<ModPacket> write = null, long sequence = 0, bool recoverable = false)
    {
        if (Main.netMode != NetmodeID.MultiplayerClient)
        {
            throw new InvalidOperationException("Multiplayer client required.");
        }

        if (operation == Op.Lookup && request == Guid.Empty)
        {
            Invoke(callback, CommandResult.Fail(CommandStatus.InvalidArg));
            return request;
        }

        if (request == Guid.Empty && !TryCreateClientOperationId(scope, recoverable, out request))
        {
            Invoke(callback, CommandResult.Fail(CommandStatus.Preparing));
            return request;
        }

        if (Requests.Count >= RuntimeConfig.MaxPendingNetworkRequests)
        {
            Invoke(callback, CommandResult.Fail(CommandStatus.Throttled));
            return request;
        }

        if (recoverable && !StageRecoverableRequest(scope, request, out sequence))
        {
            Invoke(callback, CommandResult.Fail(_clientJournal is null ? CommandStatus.Preparing : CommandStatus.PendingDelivery));
            return request;
        }

        var correlation = Guid.NewGuid();
        var pending = new Pending
        {
            Scope = scope, Operation = operation, Request = request, Sequence = sequence, Callback = callback, Write = write,
            Started = Environment.TickCount64
        };
        Requests.Add(correlation, pending);
        if (_client is not null)
        {
            SendRequest(correlation, pending);
        }

        return request;
    }

    private static void SendRequest(Guid correlation, Pending pending)
    {
        var packet = NewPacket(pending.Operation);
        WriteGuid(packet, _client.Token);
        packet.Write((byte)pending.Scope);
        WriteGuid(packet, correlation);
        WriteGuid(packet, pending.Request);
        packet.Write(pending.Sequence);
        WriteGuid(packet, _client.JournalLineage);
        pending.Write?.Invoke(packet);
        packet.Send();
        pending.Sent = true;
        pending.Write = null;
    }

    public static bool HandlePacket(BinaryReader reader, int whoAmI)
    {
        try
        {
            var op = (Op)reader.ReadByte();
            if (reader.ReadByte() != ProtocolVersion)
            {
                return true;
            }

            if (Main.netMode == NetmodeID.Server && whoAmI >= 0 && whoAmI < Main.maxPlayers)
            {
                var connectionPlayer = Main.player[whoAmI];
                GetRevision(whoAmI, out var epoch, out var revision);
                var action = ReadServerPacket(reader, op, whoAmI, revision);
                Enqueue(whoAmI, epoch, revision, connectionPlayer, action);
                return true;
            }

            if (Main.netMode == NetmodeID.MultiplayerClient)
            {
                GetRevision(-1, out var epoch, out var revision);
                Enqueue(-1, epoch, revision, null, ReadClientPacket(reader, op));
                return true;
            }
        }
        catch (Exception e) when (e is IOException or InvalidDataException or ArgumentException)
        {
            // An incomplete or unknown packet must never mutate a store or invalidate a pending result.
        }

        return true;
    }

    private static Action ReadServerPacket(BinaryReader reader, Op op, int slot, int revision)
    {
        if (op == Op.Hello)
        {
            var playerId = ReadGuid(reader);
            var recipes = ReadRecipeTable(reader);
            return () => Bind(slot, revision, playerId, recipes);
        }

        if (op == Op.ConsumeBatch)
            return ReadConsumeBatch(reader, slot);

        if (op is not (Op.Read or Op.Mutate or Op.Put or Op.DebugFill or Op.Lookup or Op.Receipt or Op.Retire or Op.RejectGrant or Op.Consume or Op.PreparePut or Op.AvailableRecipes))
        {
            return null;
        }

        var token = ReadGuid(reader);
        var scope = (Scope)reader.ReadByte();
        var correlation = ReadGuid(reader);
        var request = ReadGuid(reader);
        var sequence = reader.ReadInt64();
        var lineage = ReadGuid(reader);
        if (sequence < 0 || scope is not (Scope.Player or Scope.World) || correlation == Guid.Empty || request == Guid.Empty)
        {
            return null;
        }

        Command command = null;
        var type = 0;
        var count = 0;
        if (op is Op.Read or Op.Mutate)
        {
            command = ReadCommand(reader, op);
        }
        else if (op == Op.DebugFill)
        {
            type = reader.ReadInt32();
            count = reader.ReadInt32();
        }

        byte[] upload = null;
        if (op == Op.PreparePut)
        {
            var length = reader.ReadInt32();
            if (length <= 0 || length > MaxUploadBytes) return null;
            upload = reader.ReadBytes(length);
            if (upload.Length != length) return null;
        }
        var generation = op is Op.Receipt or Op.RejectGrant ? reader.ReadInt64() : 0L;
        return () =>
        {
            if (!Sessions.TryGetValue(slot, out var session) || session.Token != token || !IsCurrent(session))
            {
                return;
            }

            if (op == Op.AvailableRecipes)
            {
                if (sequence == 0)
                    HandleRecipeQuery(session, scope, correlation, request);
                return;
            }

            HandleRequest(session, scope, correlation, request, op, command, type, count, sequence, lineage, generation, upload);
        };
    }

    private static Action ReadClientPacket(BinaryReader reader, Op op)
    {
        if (op == Op.Welcome)
        {
            var player = ReadGuid(reader);
            var world = ReadGuid(reader);
            var token = ReadGuid(reader);
            var playerHigh = reader.ReadInt64();
            var worldHigh = reader.ReadInt64();
            var recipes = ReadRecipeTable(reader);
            var recipesAccepted = reader.ReadBoolean();
            return () =>
            {
                if (player == _clientPlayer && player != Guid.Empty && world != Guid.Empty && token != Guid.Empty && _client is null)
                {
                    var catalog = _helloRecipeCatalog;
                    _client = new Session
                    {
                        Player = player, World = world, Token = token,
                        CalibratedRecipes = recipesAccepted && RecipeTableMatches(catalog, recipes) ? catalog : null
                    };
                    OpenClientRecovery(_client, playerHigh, worldHigh);
                }
            };
        }

        if (op != Op.Result)
        {
            return null;
        }

        var sessionToken = ReadGuid(reader);
        var scope = (Scope)reader.ReadByte();
        var correlation = ReadGuid(reader);
        var request = ReadGuid(reader);
        var flags = reader.ReadByte();
        var total = reader.ReadInt32();
        var offset = reader.ReadInt32();
        if (flags is not (0 or GrantFlag or CompletedOperationFlag or PreparedFlag or RecipeAvailabilityFlag) ||
            total <= 0 || total > (flags == RecipeAvailabilityFlag ? MaxRecipeResultBytes : MaxResultBytes) ||
            offset < 0 || offset >= total || offset % FrameBytes != 0)
        {
            return null;
        }

        var length = Math.Min(FrameBytes, total - offset);
        var bytes = reader.ReadBytes(length);
        return bytes.Length == length
            ? () => AcceptFrame(sessionToken, scope, correlation, request, flags, total, offset, bytes)
            : null;
    }

    private static void GetRevision(int slot, out int epoch, out int revision)
    {
        lock (InboundGate)
        {
            epoch = _epoch;
            SlotRevisions.TryGetValue(slot, out revision);
        }
    }

    private static void Enqueue(int slot, int epoch, int revision, Player connectionPlayer, Action action)
    {
        if (action is null)
        {
            return;
        }

        lock (InboundGate)
        {
            SlotRevisions.TryGetValue(slot, out var current);
            QueuedBySlot.TryGetValue(slot, out var queued);
            var config = RuntimeConfig;
            var slotLimit = slot < 0 ? config.MaxInboundNetworkMessages : config.MaxInboundNetworkMessagesPerConnection;
            if (_epoch != epoch || revision != current || Incoming.Count >= config.MaxInboundNetworkMessages || queued >= slotLimit)
            {
                return;
            }

            QueuedBySlot[slot] = queued + 1;
            Incoming.Enqueue(new Inbound(slot, revision, connectionPlayer, action));
        }
    }

    private static void Bind(int slot, int revision, Guid playerId, RecipeTableStamp recipes)
    {
        var system = InventorySystem.Instance;
        if (playerId == Guid.Empty || system?.WorldStore is null || system.WorldId == Guid.Empty ||
            Main.player[slot]?.active != true || Netplay.Clients[slot]?.State != EnteredConnectionState)
        {
            return;
        }

        if (!Sessions.TryGetValue(slot, out var session) || !IsCurrent(session))
        {
            if (!system.TryBindServerPlayer(slot, playerId, out var playerStore))
            {
                return;
            }

            RemoveSession(slot);
            var catalog = Crafting.CraftingQueries.Catalog;
            session = new Session
            {
                Slot = slot, Player = playerId, World = system.WorldId, Token = Guid.NewGuid(),
                PlayerStore = playerStore, WorldStore = system.WorldStore, Revision = revision,
                ConnectionPlayer = Main.player[slot], JoinedRecipeCatalog = catalog,
                CalibratedRecipes = RecipeTableMatches(catalog, recipes) ? catalog : null
            };
            Sessions[slot] = session;
        }

        if (session.Player == playerId)
        {
            var packet = NewPacket(Op.Welcome);
            WriteGuid(packet, playerId);
            WriteGuid(packet, session.World);
            WriteGuid(packet, session.Token);
            packet.Write(GetLedger(session.PlayerStore).GetHighestSequence(session.Player));
            packet.Write(GetLedger(session.WorldStore).GetHighestSequence(session.Player));
            WriteRecipeTable(packet, session.JoinedRecipeCatalog);
            packet.Write(HasCalibratedRecipes(session));
            packet.Send(slot);
        }
    }

    private static bool IsCurrent(Session session)
    {
        GetRevision(session.Slot, out _, out var revision);
        return Main.netMode == NetmodeID.Server && session.Revision == revision &&
            ReferenceEquals(Main.player[session.Slot], session.ConnectionPlayer) && session.ConnectionPlayer.active &&
            Netplay.Clients[session.Slot]?.State == EnteredConnectionState &&
            ReferenceEquals(InventorySystem.Instance?.WorldStore, session.WorldStore) &&
            InventorySystem.Instance.WorldId == session.World;
    }

    /// <summary>
    /// Authorizes one server-bound Put operation for an entered connection. Call on the main thread.
    /// Reserve is called only for a new ID. It must keep its source reserved until Settle is invoked.
    /// Settle owns compensation using the exact returned remainder, including all items on failure.
    /// </summary>
    public static bool AuthorizeWorldPut(int slot, Guid request, Func<Command> reserve, Action<CommandResult> settle) =>
        AuthorizePut(Scope.World, slot, request, reserve, settle);

    public static bool AuthorizePlayerPut(int slot, Guid request, Func<Command> reserve, Action<CommandResult> settle) =>
        AuthorizePut(Scope.Player, slot, request, reserve, settle);

    private static bool AuthorizePut(Scope scope, int slot, Guid request, Func<Command> reserve, Action<CommandResult> settle)
    {
        ArgumentNullException.ThrowIfNull(reserve);
        ArgumentNullException.ThrowIfNull(settle);
        if (request == Guid.Empty || !Sessions.TryGetValue(slot, out var session) || !IsCurrent(session) ||
            session.Sources.Count >= RuntimeConfig.MaxPendingNetworkRequests || session.PutUploads.ContainsKey((scope, request)))
        {
            return false;
        }

        var store = scope == Scope.World ? session.WorldStore : session.PlayerStore;
        return store is not null && !GetLedger(store).TryGet(new TransactionLedger.Key(session.Player, request), out _) &&
            session.Sources.TryAdd((scope, request), new Source(reserve, settle));
    }

    /// <summary>
    /// Executes a trusted server Put or Move immediately on the main thread. A recorded ID is returned from
    /// the ledger without invoking the command factory. The caller supplies a stable SHA-256 digest of the
    /// operation kind, arguments and source reservation identity. Put source settlement belongs to the caller.
    /// Take goes through the delivery protocol so its result cannot also be handed out by this entry point.
    /// </summary>
    public static CommandResult SubmitTrustedWorldTransaction(int slot, Guid request, byte[] sourceFingerprint, Func<Command> create) =>
        SubmitTrustedTransaction(Scope.World, slot, request, sourceFingerprint, create);

    public static CommandResult SubmitTrustedPlayerTransaction(int slot, Guid request, byte[] sourceFingerprint, Func<Command> create) =>
        SubmitTrustedTransaction(Scope.Player, slot, request, sourceFingerprint, create);

    private static CommandResult SubmitTrustedTransaction(Scope scope, int slot, Guid request, byte[] sourceFingerprint,
        Func<Command> create)
    {
        ArgumentNullException.ThrowIfNull(create);
        if (request == Guid.Empty || sourceFingerprint?.Length != TransactionLedger.FingerprintLength ||
            !Sessions.TryGetValue(slot, out var session) || !IsCurrent(session))
        {
            return CommandResult.Fail(CommandStatus.InvalidArg);
        }

        var store = scope == Scope.World ? session.WorldStore : session.PlayerStore;
        if (store is null || session.Sources.ContainsKey((scope, request)))
        {
            return CommandResult.Fail(CommandStatus.InvalidArg);
        }

        if (IsRecoverableId(request))
            return CommandResult.Fail(CommandStatus.InvalidArg);
        if (store.IsBusy)
            return CommandResult.Fail(store.IsPreparing ? CommandStatus.Preparing : CommandStatus.PageBusy);

        var fingerprint = CreateRequestFingerprint(Op.Trusted, sourceFingerprint: sourceFingerprint);
        var entry = Execute(session, store, request, fingerprint, () =>
        {
            var command = create();
            return command?.Kind is CommandKind.Put or CommandKind.Move ? command : null;
        }, out var result);
        return entry is { Deliver: true } ? CommandResult.Fail(CommandStatus.InvalidArg) : result;
    }

    private static void HandleRequest(Session session, Scope scope, Guid correlation, Guid request, Op op,
        Command command, int type, int count, long sequence, Guid lineage, long generation, byte[] upload)
    {
        var store = scope == Scope.World ? session.WorldStore : session.PlayerStore;
        if (lineage != Guid.Empty)
        {
            if (session.JournalLineage != Guid.Empty && session.JournalLineage != lineage)
                return;
            session.JournalLineage = lineage;
        }
        if (op == Op.Receipt)
        {
            Acknowledge(session, store, request, correlation, generation);
            return;
        }

        if (Outgoing.Count >= RuntimeConfig.MaxNetworkTransfers)
        {
            // Caller retains its operation ID and can ask again. Never mutate without reply capacity.
            return;
        }

        void Reply(CommandResult result) => QueueResult(session, scope, correlation, request, 0, Encode(result));

        if (store is null)
        {
            Reply(CommandResult.Fail(CommandStatus.InternalError));
            return;
        }

        if (op == Op.Read)
        {
            if (!ValidateCommand(command, store.Config))
            {
                Reply(CommandResult.Fail(CommandStatus.InvalidArg));
                return;
            }

            store.Push(new Command(command.Kind, 0, command.Access, command.InputPayload, result =>
            {
                if (IsCurrent(session))
                {
                    Reply(result);
                }
            }));
            return;
        }

        if (_transactionRunning || store.IsBusy)
        {
            Reply(CommandResult.Fail(store.IsPreparing ? CommandStatus.Preparing : CommandStatus.PageBusy));
            return;
        }
        if (op == Op.Consume)
        {
            ConsumeEscrow(session, store, scope, correlation, request, sequence);
            return;
        }
        if (op == Op.PreparePut)
        {
            PrepareUploadedPut(session, store, scope, correlation, request, sequence, upload);
            return;
        }
        if (op == Op.RejectGrant)
        {
            RejectGrant(session, store, request, correlation, generation);
            return;
        }
        if (op == Op.Retire)
        {
            RetireOperation(session, store, request, sequence, Reply);
            return;
        }
        if (op != Op.Lookup && (IsRecoverableId(request) != (sequence > 0) || sequence > 0 && lineage == Guid.Empty))
        {
            Reply(CommandResult.Fail(CommandStatus.InvalidArg));
            return;
        }

        var fingerprint = CreateRequestFingerprint(op, command, type, count);
        if (op != Op.Lookup && fingerprint is null)
        {
            Reply(CommandResult.Fail(CommandStatus.InvalidArg));
            return;
        }

        var ledger = GetLedger(store);
        var key = new TransactionLedger.Key(session.Player, request);
        var lookup = sequence == 0 && op == Op.Lookup && ledger.TryGet(key, out _)
            ? TransactionLedger.LookupStatus.Retained : ledger.Lookup(key, sequence == 0 ? null : sequence, out _);
        if (lookup == TransactionLedger.LookupStatus.Retired)
        {
            Reply(CommandResult.Fail(CommandStatus.Retired));
            return;
        }
        if (lookup == TransactionLedger.LookupStatus.Conflict)
        {
            // An absent GUID cannot belong to an accepted transaction at this sequence.
            Reply(CommandResult.Fail(op == Op.Lookup && !ledger.TryGet(key, out _)
                ? CommandStatus.NotFound : CommandStatus.InvalidArg));
            return;
        }
        if (ledger.TryGet(key, out var entry))
        {
            if (op == Op.Lookup || entry.Matches(fingerprint))
            {
                ReplyEntry(session, store, scope, correlation, request, entry);
            }
            else
            {
                Reply(CommandResult.Fail(CommandStatus.InvalidArg));
            }

            return;
        }

        if (op == Op.Lookup)
        {
            Reply(CommandResult.Fail(CommandStatus.NotFound));
            return;
        }

        if (session.PutUploads.ContainsKey((scope, request)) ||
            op != Op.Put && session.Sources.ContainsKey((scope, request)))
        {
            Reply(CommandResult.Fail(CommandStatus.InvalidArg));
            return;
        }

        if (command?.Kind == CommandKind.Take && lineage == Guid.Empty)
        {
            Reply(CommandResult.Fail(CommandStatus.Preparing));
            return;
        }

        Source source = null;
        Func<Command> create;
        switch (op)
        {
            case Op.Mutate when command is not null:
                create = () => command;
                break;
            case Op.Put when session.Sources.Remove((scope, request), out source):
                create = source.Reserve;
                break;
            case Op.DebugFill when scope == Scope.World && ModContent.GetInstance<LargerInventoryConfig>().EnableTestCommands:
                create = () => BuildDebugFill(type, count, store.Config);
                break;
            default:
                Reply(CommandResult.Fail(CommandStatus.InvalidArg));
                return;
        }

        ExecuteAsync(session, store, request, sequence, fingerprint, create, (committed, result) =>
        {
            if (source is not null)
                Invoke(source.Settle, result);
            if (!IsCurrent(session))
                return;
            if (committed is null)
                Reply(CommandResult.Fail(result.Status));
            else
                ReplyEntry(session, store, scope, correlation, request, committed);
        });
    }

    private static void ReplyEntry(Session session, InventoryStore store, Scope scope, Guid correlation, Guid request,
        TransactionLedger.Entry entry)
    {
        if (entry.Escrow)
        {
            ReplyPrepared(session, store, scope, correlation, request, entry);
            return;
        }
        if (entry.Deliver)
        {
            ReplyServerDelivery(session, store, scope, correlation, request, entry);
            return;
        }

        QueueResult(session, scope, correlation, request, CompletedOperationFlag, entry.Result);
    }

    private static TransactionLedger GetLedger(InventoryStore store) =>
        _ledgers.GetValue(store, value =>
        {
            var ledger = new TransactionLedger(value.LoadTransactionLedger(), value.Config);
            RefreshEscrowLocks(value, ledger);
            return ledger;
        });

    internal static void OpenEscrowStore(InventoryStore store) => GetLedger(store);

    private static TransactionLedger.Entry Execute(Session session, InventoryStore store, Guid request,
        byte[] fingerprint, Func<Command> create, out CommandResult failure)
    {
        failure = CommandResult.Fail(CommandStatus.PageSaveFailed);
        if (store is null || _transactionRunning)
        {
            return null;
        }

        var ledger = GetLedger(store);
        var key = new TransactionLedger.Key(session.Player, request);
        if (ledger.TryGet(key, out var existing))
        {
            if (!existing.Matches(fingerprint))
            {
                failure = CommandResult.Fail(CommandStatus.InvalidArg);
                return null;
            }

            failure = Decode(existing.Result);
            return existing;
        }

        if (!ledger.HasRoom)
        {
            failure = CommandResult.Fail(CommandStatus.Throttled);
            return null;
        }

        Command reserved = null;
        List<Item> originalItems = null;
        TransactionLedger.Entry entry = null;
        _transactionRunning = true;
        try
        {
            reserved = create();
            if (!ValidateCommand(reserved, store.Config) || reserved.Kind is not (CommandKind.Put or CommandKind.Move))
            {
                failure = reserved is null ? CommandResult.Fail(CommandStatus.InvalidArg) :
                    Command.FailUnexecuted(reserved, CommandStatus.InvalidArg);
                return null;
            }

            object payload = reserved.InputPayload;
            if (payload is PutPayload put)
            {
                originalItems = new List<Item>(put.Items);
                // Put transfers whole stacks by reference and isolates in-place stack hooks itself.
                payload = new PutPayload { Items = originalItems, Spill = put.Spill };
            }

            var executable = new Command(reserved.Kind, 0, reserved.Access, payload);
            CommandResult committedResult = default;
            if (!store.TryExecuteTransaction(executable, result =>
                {
                    if ((result.Status & (CommandStatus.InternalError | CommandStatus.PageSaveFailed)) != 0)
                    {
                        throw new InvalidDataException("Command did not complete safely.");
                    }

                    committedResult = result;
                    // Put leftovers are returned to the trusted source by Settle, never over the wire.
                    var reply = executable.Kind == CommandKind.Put
                        ? new CommandResult(result.Status, CountItems(result.ResultPayload as List<Item>))
                        : result;
                    entry = new TransactionLedger.Entry(fingerprint, false, false, Encode(reply));
                    return ledger.Encode(key, entry);
                }, out var error))
            {
                Log.Warn("Warehouse transaction did not commit: " + error);
                failure = originalItems is null ? failure : CommandResult.Fail(failure.Status, originalItems);
                return null;
            }

            ledger.Publish(key, entry);
            failure = originalItems is not null && committedResult.ResultPayload is List<Item> { Count: > 0 }
                ? new CommandResult(committedResult.Status, originalItems)
                : committedResult;
            return entry;
        }
        catch (Exception e)
        {
            Log.Error("Warehouse transaction failed.", e);
            failure = reserved is null ? failure : Command.FailUnexecuted(reserved, failure.Status);
            return null;
        }
        finally
        {
            _transactionRunning = false;
        }
    }

    internal static bool CanAcknowledge(Guid senderToken, Guid ownerToken, Guid receivedCorrelation, Guid sentCorrelation) =>
        senderToken != Guid.Empty && senderToken == ownerToken &&
        receivedCorrelation != Guid.Empty && receivedCorrelation == sentCorrelation;

    private static void QueueResult(Session session, Scope scope, Guid correlation, Guid request, byte flags, byte[] bytes)
    {
        if (Outgoing.Count < RuntimeConfig.MaxNetworkTransfers)
        {
            Outgoing.Enqueue(new Transfer
            {
                Session = session, Scope = scope, Correlation = correlation, Request = request, Flags = flags, Bytes = bytes
            });
        }
    }

    private static void PumpTransfers()
    {
        var budget = RuntimeConfig.MaxNetworkFramesPerUpdate;
        for (var i = 0; i < budget && Outgoing.TryDequeue(out var transfer); i++)
        {
            if (!IsCurrent(transfer.Session))
            {
                continue;
            }

            var bytes = transfer.Bytes;
            var count = Math.Min(FrameBytes, bytes.Length - transfer.Offset);
            var packet = NewPacket(Op.Result);
            WriteGuid(packet, transfer.Session.Token);
            packet.Write((byte)transfer.Scope);
            WriteGuid(packet, transfer.Correlation);
            WriteGuid(packet, transfer.Request);
            packet.Write(transfer.Flags);
            packet.Write(bytes.Length);
            packet.Write(transfer.Offset);
            packet.Write(bytes, transfer.Offset, count);
            packet.Send(transfer.Session.Slot);
            transfer.Offset += count;
            if (transfer.Offset < bytes.Length)
            {
                Outgoing.Enqueue(transfer);
            }
            else if (transfer.Flags == GrantFlag)
            {
                var store = transfer.Scope == Scope.World ? transfer.Session.WorldStore : transfer.Session.PlayerStore;
                var key = new TransactionLedger.Key(transfer.Session.Player, transfer.Request);
                if (DeliveryOwners.TryGetValue((store, key), out var owner) && ReferenceEquals(owner, transfer.Session))
                {
                    transfer.Session.SentDeliveries[(store, transfer.Request)] = transfer.Correlation;
                }
            }
        }
    }

    private static void AcceptFrame(Guid token, Scope scope, Guid correlation, Guid request, byte flags, int total,
        int offset, byte[] bytes)
    {
        var session = _client;
        if (session is null || session.Token != token || !Requests.TryGetValue(correlation, out var pending) ||
            scope != pending.Scope || request != pending.Request || offset != pending.Offset ||
            (flags == RecipeAvailabilityFlag) != (pending.Operation == Op.AvailableRecipes))
        {
            return;
        }

        if (pending.Bytes is null)
        {
            pending.Bytes = new byte[total];
            pending.Flags = flags;
        }

        if (pending.Bytes.Length != total || pending.Flags != flags)
        {
            return;
        }

        Buffer.BlockCopy(bytes, 0, pending.Bytes, offset, bytes.Length);
        pending.Offset += bytes.Length;
        if (pending.Offset != total)
        {
            return;
        }

        Requests.Remove(correlation);
        if (flags == RecipeAvailabilityFlag)
        {
            Invoke(pending.Callback, DecodeRecipeAvailability(session, pending.Bytes));
            return;
        }
        if (flags == PreparedFlag)
        {
            HandleEscrowPrepared(pending, pending.Bytes);
            return;
        }
        CommandResult result;
        try
        {
            if (flags == GrantFlag)
            {
                var delivery = ServerDelivery.Read(pending.Bytes);
                var outcome = InventoryClientState.ApplyServerGrant(Main.LocalPlayer, ReceiptKey(session, scope, request), delivery,
                    session.JournalLineage);
                var applied = outcome == ServerGrantOutcome.Applied;
                if (outcome == ServerGrantOutcome.DefinitelyUnapplied)
                    SendControl(session, scope, correlation, request, Op.RejectGrant, pending.Sequence, delivery.Generation);
                var status = delivery.RemainingCount == 0 ? delivery.Status : delivery.Status | CommandStatus.Partial | CommandStatus.NoSpace;
                result = new CommandResult(applied ? status : status | CommandStatus.PendingDelivery, delivery.RemainingCount);
                if (applied)
                {
                    SendControl(session, scope, correlation, request, Op.Receipt, pending.Sequence, delivery.Generation);
                }
            }
            else
            {
                result = Decode(pending.Bytes);
            }
        }
        catch (Exception e)
        {
            Log.Error("Warehouse result or receipt processing failed.", e);
            result = CommandResult.Fail(CommandStatus.InternalError);
        }

        ObserveEscrowClientResult(pending, pending.Flags, result);
        AdvanceClientRecovery(pending, result, pending.Flags);
        Invoke(pending.Callback, result);
    }

    private static void SendControl(Session session, Scope scope, Guid correlation, Guid request, Op operation, long sequence, long generation)
    {
        var packet = NewPacket(operation);
        WriteGuid(packet, session.Token);
        packet.Write((byte)scope);
        WriteGuid(packet, correlation);
        WriteGuid(packet, request);
        packet.Write(sequence);
        WriteGuid(packet, session.JournalLineage);
        packet.Write(generation);
        packet.Send();
    }

    public static void Update()
    {
        var config = RuntimeConfig;
        var budget = Main.netMode == NetmodeID.Server ? config.MaxServerInboundMessagesPerUpdate : config.MaxInboundNetworkMessages;
        for (var i = 0; i < budget; i++)
        {
            Inbound inbound;
            lock (InboundGate)
            {
                if (!Incoming.TryDequeue(out inbound))
                {
                    break;
                }

                var queued = QueuedBySlot[inbound.Slot] - 1;
                if (queued == 0)
                {
                    QueuedBySlot.Remove(inbound.Slot);
                }
                else
                {
                    QueuedBySlot[inbound.Slot] = queued;
                }

                SlotRevisions.TryGetValue(inbound.Slot, out var revision);
                if (revision != inbound.Revision)
                {
                    continue;
                }
            }

            if (inbound.Slot >= 0 && !ReferenceEquals(Main.player[inbound.Slot], inbound.ConnectionPlayer))
            {
                continue;
            }

            try { inbound.Run(); }
            catch (Exception e) { Log.Error("Warehouse packet processing failed.", e); }
        }

        if (Main.netMode == NetmodeID.Server)
        {
            PruneSessions();
            PumpConsumeBatches();
            PumpEscrowExpiry();
            PumpTransfers();
            return;
        }

        if (Main.netMode != NetmodeID.MultiplayerClient)
        {
            if (Main.netMode == NetmodeID.SinglePlayer)
                PumpEscrowExpiry();
            return;
        }

        var now = Environment.TickCount64;
        PumpEscrowClient(now);
        PumpClientRecovery(now);
        if (_client is null && _clientPlayer != Guid.Empty && now - _helloSent >= config.NetworkHelloRetryMs)
        {
            WriteHello();
        }

        var expired = new List<Guid>();
        foreach (var pair in Requests)
        {
            if (now - pair.Value.Started >= config.NetworkRequestTimeoutMs)
            {
                expired.Add(pair.Key);
            }
            else if (_client is not null && !pair.Value.Sent)
            {
                SendRequest(pair.Key, pair.Value);
            }
        }

        foreach (var correlation in expired)
        {
            if (Requests.Remove(correlation, out var pending))
            {
                Invoke(pending.Callback, CommandResult.Fail(CommandStatus.RequestTimeout));
            }
        }
    }

    private static void PruneSessions()
    {
        List<int> disconnected = null;
        foreach (var pair in Sessions)
        {
            if (!IsCurrent(pair.Value))
            {
                (disconnected ??= new List<int>()).Add(pair.Key);
            }
        }

        if (disconnected is not null)
        {
            foreach (var slot in disconnected)
            {
                RemoveSession(slot);
            }
        }
    }

    private static void RemoveSession(int slot)
    {
        if (!Sessions.Remove(slot, out var session))
        {
            return;
        }

        foreach (var offer in session.OfferedDeliveries)
        {
            var key = (offer.Store, new TransactionLedger.Key(session.Player, offer.Request));
            if (DeliveryOwners.TryGetValue(key, out var owner) && ReferenceEquals(owner, session))
            {
                DeliveryOwners.Remove(key);
            }
        }
    }

    // Called by the main-thread store lifecycle after updating sessions and draining transfers.
    internal static bool CanClosePlayerStore(InventoryStore store)
    {
        if (EscrowStores.ContainsKey(store)) return false;
        foreach (var session in Sessions.Values)
        {
            if (ReferenceEquals(session.PlayerStore, store) && IsCurrent(session))
                return false;
        }

        foreach (var transfer in Outgoing)
        {
            if (transfer.Scope == Scope.Player && ReferenceEquals(transfer.Session.PlayerStore, store))
                return false;
        }

        foreach (var key in PendingDropSaves.Keys)
        {
            if (ReferenceEquals(key.Store, store))
                return false;
        }

        return true;
    }

    public static void ForgetPlayer(int player)
    {
        lock (InboundGate)
        {
            SlotRevisions.TryGetValue(player, out var revision);
            SlotRevisions[player] = unchecked(revision + 1);
        }
    }

    public static void CancelAll()
    {
        lock (InboundGate)
        {
            _epoch++;
            Incoming.Clear();
            SlotRevisions.Clear();
            QueuedBySlot.Clear();
        }

        Sessions.Clear();
        EscrowStores.Clear();
        ResetEscrowClient();
        DeliveryOwners.Clear();
        PendingDropSaves.Clear();
        Outgoing.Clear();
        _ledgers = new ConditionalWeakTable<InventoryStore, TransactionLedger>();
        _client = null;
        _sentRecipeHello = false;
        _helloRecipeCatalog = null;
        ActiveRecipeQueries.Clear();
        _clientJournal = null;
        _clientPlayer = Guid.Empty;
        var cancelled = new List<Pending>(Requests.Values);
        Requests.Clear();
        foreach (var pending in cancelled)
        {
            Invoke(pending.Callback, CommandResult.Fail(CommandStatus.Cancelled));
        }
    }

    internal static byte[] Encode(CommandResult result)
    {
        using var stream = new MemoryStream();
        TagIO.ToStream(CommandResultSerializer.Save(result), stream, compress: false);
        if (stream.Length > MaxResultBytes)
        {
            throw new InvalidDataException("Warehouse result exceeds the transport limit.");
        }

        return stream.ToArray();
    }

    internal static CommandResult Decode(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        var tag = TagIO.FromStream(stream, compressed: false);
        if (!CommandResultSerializer.TryLoad(tag, out var result))
        {
            throw new InvalidDataException("Invalid warehouse result.");
        }

        return result;
    }

    internal static int CountItems(List<Item> items)
    {
        if (items is null)
        {
            return 0;
        }

        long count = 0;
        foreach (var item in items)
        {
            if (item is { IsAir: false })
            {
                count += item.stack;
            }
        }

        return (int)Math.Min(count, int.MaxValue);
    }

    private static Command BuildDebugFill(int type, int count, StoreConfig config)
    {
        if (!ValidType(type) || count <= 0)
        {
            return null;
        }

        var item = new Item();
        item.SetDefaults(type);
        if (item.type != type || item.maxStack <= 0 || count > (long)item.maxStack * config.MaxRemotePutStacks)
        {
            return null;
        }

        var items = new List<Item>();
        while (count > 0)
        {
            var stack = item.Clone();
            stack.stack = Math.Min(count, item.maxStack);
            items.Add(stack);
            count -= stack.stack;
        }

        return CommandFactory.Put(items);
    }

    internal static byte[] CreateRequestFingerprint(Op op, Command command = null, int type = 0, int count = 0,
        byte[] sourceFingerprint = null)
    {
        using var buffer = new MemoryStream();
        using var writer = new BinaryWriter(buffer);
        writer.Write((byte)op);
        switch (op)
        {
            case Op.Mutate when command is not null && command.Kind is CommandKind.Move or CommandKind.Take:
                if (!WriteCommand(writer, command))
                {
                    return null;
                }
                break;
            case Op.DebugFill:
                writer.Write(type);
                writer.Write(count);
                break;
            case Op.Put:
                // No client fields besides the operation ID, which keys a single server reservation.
                break;
            case Op.Trusted when sourceFingerprint?.Length == TransactionLedger.FingerprintLength:
                writer.Write(sourceFingerprint);
                break;
            default:
                return null;
        }

        return SHA256.HashData(buffer.GetBuffer().AsSpan(0, (int)buffer.Length));
    }

    // Fingerprints omit runtime budgets so existing requests can still be replayed after a limit changes.
    private static bool WriteCommand(BinaryWriter writer, Command command, StoreConfig config = null)
    {
        if (!ValidateCommand(command, config) || command.Kind == CommandKind.Put)
        {
            return false;
        }

        writer.Write((byte)command.Kind);
        switch (command.InputPayload)
        {
            case QueryTypePayload query:
                writer.Write(query.Type);
                break;
            case QuerySlotPayload slots:
                WriteSlots(writer, slots.Slots);
                break;
            case MovePayload move:
                writer.Write(move.SourceSlot);
                writer.Write(move.DestinationSlot);
                break;
            case TakePayload take:
                writer.Write(take.Type);
                writer.Write(take.Count);
                writer.Write(take.AvoidFavorited);
                WriteSlots(writer, command.Access.IsStrong ? command.Access.Slots : Array.Empty<int>());
                break;
        }

        return true;
    }

    private static Command ReadCommand(BinaryReader reader, Op op)
    {
        switch ((CommandKind)reader.ReadByte())
        {
            case CommandKind.QueryType when op == Op.Read:
                return CommandFactory.QueryType(reader.ReadInt32());
            case CommandKind.QuerySlot when op == Op.Read:
                return CommandFactory.QuerySlot(ReadSlots(reader));
            case CommandKind.Move when op == Op.Mutate:
                return CommandFactory.Move(reader.ReadInt32(), reader.ReadInt32());
            case CommandKind.Take when op == Op.Mutate:
            {
                var type = reader.ReadInt32();
                var count = reader.ReadInt32();
                var avoid = reader.ReadBoolean();
                var slots = ReadSlots(reader);
                if (slots.Length == 0)
                {
                    return CommandFactory.Take(type, count, avoid);
                }

                return new Command(CommandKind.Take, 0, AccessSet.Strong(slots),
                    new TakePayload { Type = type, Count = count, AvoidFavorited = avoid });
            }
            default:
                return null;
        }
    }

    private static bool ValidateCommand(Command command, StoreConfig config = null)
    {
        if (command is null)
        {
            return false;
        }

        return command.Kind switch
        {
            CommandKind.QueryType when command.InputPayload is QueryTypePayload query => ValidType(query.Type),
            CommandKind.QuerySlot when command.InputPayload is QuerySlotPayload slots => ValidSlots(slots.Slots, config),
            CommandKind.Move when command.InputPayload is MovePayload move => move.SourceSlot != move.DestinationSlot &&
                ValidSlot(move.SourceSlot) && ValidSlot(move.DestinationSlot),
            CommandKind.Take when command.InputPayload is TakePayload take =>
                take.Predicate is null && take.DestinationSnapshot is null && ValidType(take.Type) &&
                take.Count > 0 && (config is null || take.Count <= config.MaxRemoteTakeItems) &&
                (!command.Access.IsStrong || ValidSlots(command.Access.Slots, config)),
            CommandKind.Put when config is not null && command.InputPayload is PutPayload put =>
                ValidPut(put, config.MaxRemotePutStacks) && (!command.Access.IsStrong || ValidSlots(command.Access.Slots, config)),
            _ => false
        };
    }

    private static bool ValidPut(PutPayload put, int maxStacks)
    {
        if (put.Items is null || put.Items.Count == 0 || put.Items.Count > maxStacks)
        {
            return false;
        }

        long stacks = 0;
        foreach (var item in put.Items)
        {
            if (item is null || item.IsAir || !ValidType(item.type) || item.stack <= 0 || item.maxStack <= 0)
            {
                return false;
            }

            stacks += (item.stack + (long)item.maxStack - 1) / item.maxStack;
            if (stacks > maxStacks)
            {
                return false;
            }
        }

        return true;
    }

    private static bool ValidType(int type) => type > 0 && type < ItemLoader.ItemCount;

    private static bool ValidSlot(int slot) => slot >= 0 && slot / ItemPage.SlotCount < PageDirectory.MaxPageCount;

    private static bool ValidSlots(int[] slots, StoreConfig config)
    {
        if (slots is null || slots.Length == 0 || slots.Length > MaxWireSlots || (config is not null && slots.Length > config.MaxRemoteCommandSlots))
        {
            return false;
        }

        var seen = new HashSet<int>();
        foreach (var slot in slots)
        {
            if (!ValidSlot(slot) || !seen.Add(slot))
            {
                return false;
            }
        }

        return true;
    }

    private static int[] ReadSlots(BinaryReader reader)
    {
        var count = reader.ReadUInt16();
        if (count > MaxWireSlots)
        {
            throw new InvalidDataException("Too many warehouse slots.");
        }

        var slots = new int[count];
        for (var i = 0; i < count; i++)
        {
            slots[i] = reader.ReadInt32();
        }

        return slots;
    }

    private static void WriteSlots(BinaryWriter writer, int[] slots)
    {
        writer.Write((ushort)slots.Length);
        foreach (var slot in slots)
        {
            writer.Write(slot);
        }
    }

    private static ModPacket NewPacket(Op op)
    {
        var packet = LargerInventory.Instance.GetPacket();
        packet.Write((byte)op);
        packet.Write(ProtocolVersion);
        return packet;
    }

    private static Guid ReadGuid(BinaryReader reader) => new(reader.ReadBytes(16));
    private static void WriteGuid(BinaryWriter writer, Guid value) => writer.Write(value.ToByteArray());

    private static void Invoke(Action<CommandResult> callback, CommandResult result)
    {
        try { callback?.Invoke(result); }
        catch (Exception e) { Log.Error("Warehouse callback failed.", e); }
    }
}
