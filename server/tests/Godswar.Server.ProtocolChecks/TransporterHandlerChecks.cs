using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Reflection;
using Godswar.Server.Application.Accounts;
using Godswar.Server.Application.World;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Game;
using Godswar.Server.Networking;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class TransporterHandlerChecks
{
    public const string CheckName =
        "Ordinary Transporter native handler flow";

    private const int AccountId = 631;
    private const int CharacterId = 6_331;
    private const uint LocalPlayerObjectId = 0x1448;
    private const float PortalRadius = 6f;

    private static readonly MethodInfo HandlePacketMethod =
        RequiredMethod("HandlePacketAsync");
    private static readonly MethodInfo InstallNpcCatalogMethod =
        RequiredMethod("InstallNpcCatalog");
    private static readonly MethodInfo StopRealtimeMethod =
        RequiredMethod("StopRealtimeMovementAsync");

    public static async Task RunAsync()
    {
        await CheckNativeOpenAndMenusAsync();
        await CheckLevelRequirementResultsAsync();
        await CheckSuccessfulTransitionAsync();
        await CheckForgedActionsFailClosedAsync();
    }

    private static async Task CheckSuccessfulTransitionAsync()
    {
        var endpoint = Sparta();
        await using var fixture = await CreateFixtureAsync(
            endpoint,
            level: 1);
        var destination = TransporterProtocol.Destinations.Single(
            static candidate =>
                candidate.NpcKey == "Sparta_042" &&
                candidate.SubId == 1);
        var traversal = GameplayContentTestFixtures.Runtime.MapTraversal;
        Check.True(
            traversal.TryGetAutomaticLink(
                destination.ArrivalAnchorSourceMapId,
                destination.TargetMapId,
                out var entryAnchor),
            "Sparta Transporter destination has a reviewed entry anchor");
        Check.True(
            traversal.TryResolveTargetArrival(
                entryAnchor,
                PortalRadius,
                out var arrival),
            "Sparta Transporter destination has a reviewed safe arrival");

        await IssueTransporterMenuAsync(fixture, endpoint);
        var packetCount = fixture.ReadPackets().Count;
        await InvokeAsync(
            fixture.Handler,
            CreateActionPacket(endpoint.InteractionId, destination.SubId));

        Check.True(
            fixture.ReadPackets().Skip(packetCount).ToArray() is
                [var sceneChange] &&
            sceneChange.SequenceEqual(PacketBuilder.SceneChange(
                LocalPlayerObjectId,
                arrival.TargetArrival.X,
                y: 0f,
                arrival.TargetArrival.Z,
                checked((byte)destination.TargetMapId))),
            "eligible Transporter travel emits the exact native SceneChange");
        Check.True(
            fixture.Store.PositionWrites is [var write] &&
            write.AccountId == AccountId &&
            write.CharacterId == CharacterId &&
            write.MapId == destination.TargetMapId &&
            write.X == arrival.TargetArrival.X &&
            write.Z == arrival.TargetArrival.Z,
            "Transporter travel persists its reviewed arrival exactly once");
        Check.True(
            fixture.Character.CurrentMap == destination.TargetMapId &&
            fixture.Character.PositionX == arrival.TargetArrival.X &&
            fixture.Character.PositionZ == arrival.TargetArrival.Z &&
            fixture.Registry.GetMapPopulation(endpoint.SourceMapId) == 0 &&
            fixture.Registry.GetMapPopulation(
                checked((byte)destination.TargetMapId)) == 1 &&
            !fixture.Registry.GetMapSessions(
                checked((byte)destination.TargetMapId)).Any(
                context => ReferenceEquals(context.Session, fixture.Session)),
            "Transporter travel transfers authority and hides the pending scene");
    }

    private static async Task CheckForgedActionsFailClosedAsync()
    {
        await using (var fixture = await CreateFixtureAsync(
                         Sparta(),
                         level: 140))
        {
            await AssertRejectedAsync(
                fixture,
                CreateActionPacket(
                    TransporterProtocol.SpartaNpcId,
                    subId: 1),
                "selection without a menu lease");
            await IssueTransporterMenuAsync(fixture, Sparta());
            await AssertRejectedAsync(
                fixture,
                CreateActionPacket(
                    TransporterProtocol.AthensNpcId,
                    subId: 4),
                "wrong endpoint");
            await AssertRejectedAsync(
                fixture,
                CreateActionPacket(
                    TransporterProtocol.SpartaNpcId,
                    subId: 1,
                    configureArguments: static arguments =>
                        arguments[0] = 0),
                "non-empty action path");
            await AssertRejectedAsync(
                fixture,
                CreateActionPacket(
                    TransporterProtocol.SpartaNpcId,
                    subId: 1,
                    duplicateDialogIndex: 2),
                "forged duplicate dialog");

            fixture.Character.PositionX +=
                TransporterProtocol.MaximumInteractionDistance + 1f;
            await AssertRejectedAsync(
                fixture,
                CreateActionPacket(
                    TransporterProtocol.SpartaNpcId,
                    subId: 1),
                "selection outside interaction distance");
        }

        var wrongMapEndpoint = Sparta() with { SourceMapId = 1 };
        await using var wrongMap = await CreateFixtureAsync(
            wrongMapEndpoint,
            level: 140);
        await IssueTransporterMenuAsync(wrongMap, wrongMapEndpoint);
        await AssertRejectedAsync(
            wrongMap,
            CreateActionPacket(
                TransporterProtocol.SpartaNpcId,
                subId: 1),
            "wrong source map");

        await using var dead = await CreateFixtureAsync(
            Sparta(),
            level: 140);
        dead.Character.CurrentHp = 0;
        await InvokeAsync(
            dead.Handler,
            CreateActionPacket(
                TransporterProtocol.SpartaNpcId,
                TransporterProtocol.InitialRequestSubId));
        Check.True(
            dead.ReadPackets().Count == 0,
            "dead characters cannot acquire a Transporter menu lease");
        await AssertRejectedAsync(
            dead,
            CreateActionPacket(
                TransporterProtocol.SpartaNpcId,
                subId: 1),
            "dead-character selection");
    }

    private static async Task IssueTransporterMenuAsync(
        Fixture fixture,
        Endpoint endpoint) =>
        await InvokeAsync(
            fixture.Handler,
            CreateActionPacket(
                endpoint.InteractionId,
                TransporterProtocol.InitialRequestSubId));

    private static async Task AssertRejectedAsync(
        Fixture fixture,
        GamePacket packet,
        string description)
    {
        var packetCount = fixture.ReadPackets().Count;
        var writeCount = fixture.Store.PositionWrites.Count;
        var sourceMap = fixture.Character.CurrentMap;
        await InvokeAsync(fixture.Handler, packet);
        Check.True(
            fixture.ReadPackets().Count == packetCount &&
            fixture.Store.PositionWrites.Count == writeCount &&
            fixture.Character.CurrentMap == sourceMap &&
            fixture.Registry.GetMapSessions(sourceMap).Any(context =>
                ReferenceEquals(context.Session, fixture.Session)),
            $"Transporter {description} is rejected without output or mutation");
    }

    private static async Task<Fixture> CreateFixtureAsync(
        Endpoint endpoint,
        int level)
    {
        var character = new GameCharacter
        {
            Id = CharacterId,
            AccountId = AccountId,
            Name = "TransporterHandlerHero",
            CreatedUtc = new DateTime(2026, 9, 1, 0, 0, 0,
                DateTimeKind.Utc),
            Camp = endpoint.SourceMapId == 1
                ? GameDefaults.AthensCamp
                : GameDefaults.SpartaCamp,
            CurrentMap = endpoint.SourceMapId,
            PositionX = 10f,
            PositionZ = 20f,
            Level = level,
            CurrentHp = 2_000,
            MaxHp = 2_500,
            CurrentMp = 1_000,
            MaxMp = 1_500,
            Equipment = string.Empty,
            KitBag = string.Empty
        };
        var npc = new NpcSpawnDefinition(
            endpoint.SourceMapId,
            endpoint.SceneKey,
            endpoint.NpcKey,
            $"{endpoint.NpcKey}_Transporter",
            endpoint.InteractionId,
            character.PositionX,
            character.PositionZ,
            endpoint.InteractionId,
            AppearanceType: 1,
            Facing: 0f,
            Detail10077: [],
            Detail10080: []);
        var route = new NpcDialogueRouteDefinition(
            endpoint.NpcKey,
            endpoint.NpcKey,
            TransporterProtocol.DialogIndex,
            NpcDialogueBehavior.Transporter,
            ImmutableArray.CreateRange(endpoint.Menu));
        var worldContent = PinnedWorldContentReader.Create(
            "transporter-handler-v1",
            [endpoint.SourceMapId],
            [npc],
            [],
            [],
            new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero),
            npcTexts:
            [
                new NpcTextDefinition(
                    npc.NpcKey,
                    npc.SceneKey,
                    "Transporter",
                    "Transporter handler test dialogue")
            ],
            npcDialogueRoutes: [route],
            gameplay: GameplayContentTestFixtures.Published);
        var store = new TransporterStore();
        var transport = new FactionCrierCaptureTransport();
        var session = new ClientSession(transport);
        var registry = new GameSessionRegistry(
            gameplayCatalogs: GameplayContentTestFixtures.Runtime);
        GameHandlerOwnershipTestFences.Bind(
            registry,
            session,
            AccountId,
            character);
        registry.JoinMap(
            session,
            AccountId,
            character,
            WorldObjectIds.ForPlayer(CharacterId),
            worldReady: true);
        var handler = new GameClientHandler(
            session,
            store,
            registry,
            CharacterSnapshotReaderTestFixtures.Unused,
            worldContent,
            gameplayCatalogs: GameplayContentTestFixtures.Runtime);
        SetHandlerField(
            handler,
            "_account",
            new AccountIdentity(AccountId, "transporter-handler-check"));
        SetHandlerField(handler, "_character", character);
        SetHandlerField(handler, "_registered", true);
        SetHandlerField(handler, "_worldPresenceAnnounced", true);

        var catalog = await registry.PublishMapNpcDefinitionsAsync(
            endpoint.SourceMapId,
            [npc],
            originSession: null,
            CancellationToken.None);
        InstallNpcCatalogMethod.Invoke(handler, [catalog]);
        var visibility = GetHandlerField<WorldSectorVisibilityTracker<
            NpcSpawnDefinition>>(handler, "_npcVisibility") ??
            throw new InvalidOperationException(
                "Transporter NPC visibility was not installed.");
        Check.True(
            visibility.TryCalculate(
                character.PositionX,
                character.PositionZ,
                out var delta),
            "Transporter NPC visibility calculates");
        visibility.Commit(delta);

        return new Fixture(
            session,
            transport,
            handler,
            registry,
            store,
            character);
    }

    private static GamePacket CreateDialogOpenPacket(uint npcId)
    {
        var bytes = new byte[48];
        BinaryPrimitives.WriteUInt16LittleEndian(
            bytes,
            checked((ushort)bytes.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(
            bytes.AsSpan(2),
            Opcodes.NpcDialogOpen);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), npcId);
        return new GamePacket(bytes);
    }

    private static GamePacket CreateActionPacket(
        uint npcId,
        int subId,
        Action<int[]>? configureArguments = null,
        int? duplicateDialogIndex = null)
    {
        var arguments = Enumerable.Repeat(
            -1,
            TransporterProtocol.FunctionArgumentCount).ToArray();
        configureArguments?.Invoke(arguments);
        var bytes = new byte[TransporterProtocol.ActionPacketBytes];
        BinaryPrimitives.WriteUInt16LittleEndian(
            bytes,
            TransporterProtocol.ActionPacketBytes);
        BinaryPrimitives.WriteUInt16LittleEndian(
            bytes.AsSpan(2),
            Opcodes.NpcFunctionAction);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), npcId);
        BinaryPrimitives.WriteInt32LittleEndian(
            bytes.AsSpan(8),
            TransporterProtocol.DialogIndex);
        BinaryPrimitives.WriteInt32LittleEndian(
            bytes.AsSpan(12),
            duplicateDialogIndex ?? TransporterProtocol.DialogIndex);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(16), subId);
        for (var index = 0; index < arguments.Length; index++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(
                bytes.AsSpan(20 + (index * sizeof(int))),
                arguments[index]);
        }
        return new GamePacket(bytes);
    }

    private static async Task InvokeAsync(
        GameClientHandler handler,
        GamePacket packet)
    {
        var task = HandlePacketMethod.Invoke(
            handler,
            [packet, CancellationToken.None]) as Task ??
            throw new InvalidOperationException(
                "Transporter handler did not return a task.");
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
        string name) => (T?)(typeof(GameClientHandler).GetField(
            name,
            BindingFlags.Instance | BindingFlags.NonPublic) ??
        throw new InvalidOperationException(
            $"GameClientHandler.{name} was not found.")).GetValue(handler);

    private static Endpoint[] Endpoints() =>
        [Sparta(), Athens(), Mycenae()];

    private static Endpoint Sparta() => new(
        "Sparta_042",
        "Sparta",
        TransporterProtocol.SpartaNpcId,
        0,
        TransporterProtocol.SpartaInitialMenuSubIds.ToArray());

    private static Endpoint Athens() => new(
        "Athens_041",
        "Athens",
        TransporterProtocol.AthensNpcId,
        1,
        TransporterProtocol.AthensInitialMenuSubIds.ToArray());

    private static Endpoint Mycenae() => new(
        "Mycenae_All_013",
        "Mycenae_All",
        TransporterProtocol.MycenaeNpcId,
        6,
        TransporterProtocol.MycenaeInitialMenuSubIds.ToArray());

    private sealed record Endpoint(
        string NpcKey,
        string SceneKey,
        uint InteractionId,
        byte SourceMapId,
        int[] Menu);

    private readonly record struct PositionWrite(
        int AccountId,
        int CharacterId,
        byte MapId,
        float X,
        float Z);

    private sealed class TransporterStore : GameStoreTestStub
    {
        public List<PositionWrite> PositionWrites { get; } = [];

        public override Task SaveCharacterPositionAsync(
            int accountId,
            int characterId,
            byte currentMap,
            float positionX,
            float positionZ,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PositionWrites.Add(new PositionWrite(
                accountId,
                characterId,
                currentMap,
                positionX,
                positionZ));
            return Task.CompletedTask;
        }
    }

    private sealed record Fixture(
        ClientSession Session,
        FactionCrierCaptureTransport Transport,
        GameClientHandler Handler,
        GameSessionRegistry Registry,
        TransporterStore Store,
        GameCharacter Character) : IAsyncDisposable
    {
        public IReadOnlyList<byte[]> ReadPackets() =>
            Transport.ReadLegacyPackets();

        public async ValueTask DisposeAsync()
        {
            var stopTask = StopRealtimeMethod.Invoke(Handler, null) as Task ??
                throw new InvalidOperationException(
                    "Transporter handler stop did not return a task.");
            await stopTask;
            Registry.Remove(Session);
            await Session.DisposeAsync();
        }
    }
}
