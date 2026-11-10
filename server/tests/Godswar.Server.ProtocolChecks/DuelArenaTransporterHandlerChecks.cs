using System.Buffers.Binary;
using System.Reflection;
using Godswar.Server.Application.Accounts;
using Godswar.Server.Application.World;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Game;
using Godswar.Server.Infrastructure.WorldContent;
using Godswar.Server.Networking;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class DuelArenaTransporterHandlerChecks
{
    public const string CheckName =
        "Duel Arena same-scene transporter handler";

    private const int AccountId = 641;
    private const int CharacterId = 6_341;
    private const uint LocalPlayerObjectId = 0x1448;

    private static readonly MethodInfo HandlePacketMethod =
        RequiredMethod("HandlePacketAsync");
    private static readonly MethodInfo InstallNpcCatalogMethod =
        RequiredMethod("InstallNpcCatalog");
    private static readonly MethodInfo StopRealtimeMethod =
        RequiredMethod("StopRealtimeMovementAsync");

    public static async Task RunAsync()
    {
        await CheckSuccessfulPairAsync();
        await CheckSequentialRoundTripAsync();
        await CheckReplayAndForgedActionsAsync();
        await CheckDeadDistanceAndWorldFencesAsync();
        await CheckTransitionCompletionAndObserverRemovalAsync();
        await CheckMalformedExpiredAndPersistenceFailureAsync();
        await CheckPostPersistenceSceneFenceAsync();
        await CheckCapturedTravelAsync();
        await CheckCapturedServicesAsync();
        await CheckDoorkeeperExitAsync();
    }

    private static async Task CheckSuccessfulPairAsync()
    {
        foreach (var endpoint in Endpoints())
        {
            await using var fixture = await CreateFixtureAsync(endpoint);
            await IssueMenuAsync(fixture, endpoint);
            var packetCount = fixture.ReadPackets().Count;

            await InvokeAsync(
                fixture.Handler,
                CreateActionPacket(endpoint.InteractionId));

            var emitted = fixture.ReadPackets()
                .Skip(packetCount)
                .ToArray();
            Check.True(
                emitted.Count(static packet =>
                    ReadOpcode(packet) == Opcodes.SceneChange) == 1 &&
                emitted.Single(static packet =>
                    ReadOpcode(packet) == Opcodes.SceneChange)
                    .SequenceEqual(PacketBuilder.SceneChange(
                        LocalPlayerObjectId,
                        endpoint.TargetX,
                        y: 0f,
                        endpoint.TargetZ,
                        DuelArenaTransporterProtocol.MapId)),
                $"{endpoint.NpcKey} emits the exact same-map SceneChange");
            Check.True(
                fixture.Store.PositionWrites is [var write] &&
                write.AccountId == AccountId &&
                write.CharacterId == CharacterId &&
                write.MapId == DuelArenaTransporterProtocol.MapId &&
                write.X == endpoint.TargetX &&
                write.Z == endpoint.TargetZ &&
                fixture.Character.CurrentMap ==
                    DuelArenaTransporterProtocol.MapId &&
                fixture.Character.PositionX == endpoint.TargetX &&
                fixture.Character.PositionZ == endpoint.TargetZ &&
                GetHandlerField<DuelArenaTransporterDialogueContext>(
                    fixture.Handler,
                    "_duelArenaTransporterDialogueContext") is null &&
                !fixture.Registry.GetMapSessions(
                    DuelArenaTransporterProtocol.MapId).Any(context =>
                        ReferenceEquals(
                            context.Session,
                            fixture.Session)),
                $"{endpoint.NpcKey} persists and applies its reviewed " +
                "same-map arrival exactly once while hiding until ready");

            var afterTransition = fixture.ReadPackets().Count;
            await InvokeAsync(
                fixture.Handler,
                CreateActionPacket(endpoint.InteractionId));
            Check.True(
                fixture.ReadPackets().Count == afterTransition &&
                fixture.Store.PositionWrites.Count == 1,
                $"{endpoint.NpcKey} action lease cannot be replayed");
        }
    }

    private static async Task CheckReplayAndForgedActionsAsync()
    {
        var endpoint = Gatekeeper();
        await using var fixture = await CreateFixtureAsync(endpoint);
        await AssertRejectedAsync(
            fixture,
            CreateActionPacket(endpoint.InteractionId),
            "selection without a menu lease");

        await IssueMenuAsync(fixture, endpoint);
        await AssertRejectedAsync(
            fixture,
            CreateActionPacket(
                DuelArenaTransporterProtocol.DoorkeeperNpcId),
            "wrong endpoint identity");
        await AssertRejectedAsync(
            fixture,
            CreateActionPacket(
                endpoint.InteractionId,
                configureArguments: static values => values[0] = 0),
            "non-empty action path");
        await AssertRejectedAsync(
            fixture,
            CreateActionPacket(
                endpoint.InteractionId,
                duplicateDialogIndex: 2),
            "forged duplicate dialog index");
    }

    private static async Task CheckDeadDistanceAndWorldFencesAsync()
    {
        var endpoint = Gatekeeper();
        await using (var dead = await CreateFixtureAsync(endpoint))
        {
            dead.Character.CurrentHp = 0;
            await InvokeAsync(
                dead.Handler,
                CreateActionPacket(
                    endpoint.InteractionId,
                    DuelArenaTransporterProtocol.InitialRequestSubId));
            Check.True(
                dead.ReadPackets().Count == 0,
                "dead characters cannot acquire an Arena menu lease");
            await AssertRejectedAsync(
                dead,
                CreateActionPacket(endpoint.InteractionId),
                "dead-character selection");
        }

        await using (var distant = await CreateFixtureAsync(endpoint))
        {
            await IssueMenuAsync(distant, endpoint);
            distant.Character.PositionX +=
                DuelArenaTransporterProtocol.MaximumInteractionDistance +
                1f;
            distant.Registry.UpdateCharacter(
                distant.Session,
                distant.Character,
                advanceWorldRevision: false);
            await AssertRejectedAsync(
                distant,
                CreateActionPacket(endpoint.InteractionId),
                "selection outside interaction distance");
        }

        await using var wrongWorld = await CreateFixtureAsync(endpoint);
        await IssueMenuAsync(wrongWorld, endpoint);
        var context = GetHandlerField<
            DuelArenaTransporterDialogueContext>(
            wrongWorld.Handler,
            "_duelArenaTransporterDialogueContext") ??
            throw new InvalidOperationException(
                "Arena transporter lease was not issued.");
        SetHandlerField(
            wrongWorld.Handler,
            "_duelArenaTransporterDialogueContext",
            context with { SourceWorldInstanceId = WorldInstanceId.New() });
        await AssertRejectedAsync(
            wrongWorld,
            CreateActionPacket(endpoint.InteractionId),
            "selection from a different world instance");
    }

    private static async Task IssueMenuAsync(
        Fixture fixture,
        Endpoint endpoint)
    {
        await InvokeAsync(
            fixture.Handler,
            CreateActionPacket(
                endpoint.InteractionId,
                DuelArenaTransporterProtocol.InitialRequestSubId));
        Check.True(
            fixture.ReadPackets().LastOrDefault() is { } menu &&
            menu.SequenceEqual(PacketBuilder.NpcFunctionActionResponse(
                endpoint.InteractionId,
                DuelArenaTransporterProtocol.DialogIndex,
                DuelArenaTransporterProtocol.InitialMenuSubIds.ToArray())),
            $"{endpoint.NpcKey} issues its native finite menu");
    }

    private static async Task AssertRejectedAsync(
        Fixture fixture,
        GamePacket packet,
        string description)
    {
        var packetCount = fixture.ReadPackets().Count;
        var writeCount = fixture.Store.PositionWrites.Count;
        var position = (
            fixture.Character.CurrentMap,
            fixture.Character.PositionX,
            fixture.Character.PositionZ);
        await InvokeAsync(fixture.Handler, packet);
        Check.True(
            fixture.ReadPackets().Count == packetCount &&
            fixture.Store.PositionWrites.Count == writeCount &&
            position == (
                fixture.Character.CurrentMap,
                fixture.Character.PositionX,
                fixture.Character.PositionZ),
            $"Arena {description} is rejected without output or mutation");
    }

    private static GamePacket CreateActionPacket(
        uint npcId,
        int subId = DuelArenaTransporterProtocol.TravelSubId,
        Action<int[]>? configureArguments = null,
        int? duplicateDialogIndex = null,
        int? declaredLength = null,
        int? bufferLength = null)
    {
        var arguments = Enumerable.Repeat(
            -1,
            DuelArenaTransporterProtocol.FunctionArgumentCount).ToArray();
        configureArguments?.Invoke(arguments);
        var bytes = new byte[
            DuelArenaTransporterProtocol.ActionPacketBytes];
        BinaryPrimitives.WriteUInt16LittleEndian(
            bytes,
            DuelArenaTransporterProtocol.ActionPacketBytes);
        BinaryPrimitives.WriteUInt16LittleEndian(
            bytes.AsSpan(2),
            Opcodes.NpcFunctionAction);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), npcId);
        BinaryPrimitives.WriteInt32LittleEndian(
            bytes.AsSpan(8),
            DuelArenaTransporterProtocol.DialogIndex);
        BinaryPrimitives.WriteInt32LittleEndian(
            bytes.AsSpan(12),
            duplicateDialogIndex ??
                DuelArenaTransporterProtocol.DialogIndex);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(16), subId);
        for (var index = 0; index < arguments.Length; index++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(
                bytes.AsSpan(20 + (index * sizeof(int))),
                arguments[index]);
        }
        Array.Resize(
            ref bytes,
            bufferLength ?? DuelArenaTransporterProtocol.ActionPacketBytes);
        BinaryPrimitives.WriteUInt16LittleEndian(
            bytes,
            checked((ushort)(declaredLength ?? bytes.Length)));
        return new GamePacket(bytes);
    }

    private static ushort ReadOpcode(byte[] packet) =>
        packet.Length >= 4
            ? BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(2))
            : (ushort)0;

    private static async Task InvokeAsync(
        GameClientHandler handler,
        GamePacket packet)
    {
        var task = HandlePacketMethod.Invoke(
            handler,
            [packet, CancellationToken.None]) as Task ??
            throw new InvalidOperationException(
                "Arena handler did not return a task.");
        await task;
    }

    private static MethodInfo RequiredMethod(string name) =>
        typeof(GameClientHandler).GetMethod(
            name,
            BindingFlags.Instance | BindingFlags.NonPublic) ??
        throw new InvalidOperationException(
            $"GameClientHandler.{name} was not found.");

    private static void SetHandlerField<T>(
        GameClientHandler handler,
        string name,
        T value) =>
        (typeof(GameClientHandler).GetField(
            name,
            BindingFlags.Instance | BindingFlags.NonPublic) ??
         throw new InvalidOperationException(
             $"GameClientHandler.{name} was not found."))
        .SetValue(handler, value);

    private static T? GetHandlerField<T>(
        GameClientHandler handler,
        string name) =>
        (T?)(typeof(GameClientHandler).GetField(
            name,
            BindingFlags.Instance | BindingFlags.NonPublic) ??
        throw new InvalidOperationException(
            $"GameClientHandler.{name} was not found.")).GetValue(handler);

    private static Endpoint[] Endpoints() =>
        [Gatekeeper(), Doorkeeper()];

    private static Endpoint Gatekeeper()
    {
        var spawn = Spawn(DuelArenaTransporterProtocol.GatekeeperNpcKey);
        return new(
            spawn,
            DuelArenaTransporterProtocol.LowerArrivalX,
            DuelArenaTransporterProtocol.LowerArrivalZ);
    }

    private static Endpoint Doorkeeper()
    {
        var spawn = Spawn(DuelArenaTransporterProtocol.DoorkeeperNpcKey);
        return new(
            spawn,
            DuelArenaTransporterProtocol.UpperArrivalX,
            DuelArenaTransporterProtocol.UpperArrivalZ);
    }

    private static NpcSpawnDefinition Spawn(string npcKey) =>
        NpcContentBaselineV6.LoadDefinitions()
            .Single(candidate => candidate.NpcKey == npcKey);

    private sealed record Endpoint(
        NpcSpawnDefinition Spawn,
        float TargetX,
        float TargetZ)
    {
        public string NpcKey => Spawn.NpcKey;
        public uint InteractionId => Spawn.InteractionId;
    }

    private readonly record struct PositionWrite(
        int AccountId,
        int CharacterId,
        byte MapId,
        float X,
        float Z);

    private sealed class ArenaStore : GameStoreTestStub
    {
        public List<PositionWrite> PositionWrites { get; } = [];

        public bool RejectPositionWrite { get; set; }

        public Action? AfterPositionWrite { get; set; }

        public override Task<CharacterStats?> GetCharacterStatsAsync(
            int accountId,
            int characterId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<CharacterStats?>(null);
        }

        public override Task SaveCharacterPositionAsync(
            int accountId,
            int characterId,
            byte currentMap,
            float positionX,
            float positionZ,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (RejectPositionWrite)
            {
                throw new InvalidOperationException(
                    "Arena position persistence failed by test design.");
            }
            PositionWrites.Add(new(
                accountId,
                characterId,
                currentMap,
                positionX,
                positionZ));
            AfterPositionWrite?.Invoke();
            return Task.CompletedTask;
        }
    }

    private sealed record Fixture(
        ClientSession Session,
        FactionCrierCaptureTransport Transport,
        GameClientHandler Handler,
        GameSessionRegistry Registry,
        ArenaStore Store,
        GameCharacter Character) : IAsyncDisposable
    {
        public IReadOnlyList<byte[]> ReadPackets() =>
            Transport.ReadLegacyPackets();

        public async ValueTask DisposeAsync()
        {
            var stopTask = StopRealtimeMethod.Invoke(Handler, null) as Task ??
                throw new InvalidOperationException(
                    "Arena handler stop did not return a task.");
            await stopTask;
            Registry.Remove(Session);
            await Session.DisposeAsync();
        }
    }
}
