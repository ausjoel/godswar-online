using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Reflection;
using Godswar.Server.Application.Accounts;
using Godswar.Server.Application.Characters;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.FactionCrier;
using Godswar.Server.Application.Inventory;
using Godswar.Server.Application.Realms;
using Godswar.Server.Application.World;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Game;
using Godswar.Server.Networking;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class FactionCrierHandlerChecks
{
    private const int AccountId = 7;
    private const int CharacterId = 19;
    private const int RenewalSlot = 25;
    private const int RenewalCoordinate = 101;
    private const long BeforeFactionRevision = 10;
    private const long AfterFactionRevision = 11;
    private const long BeforeInventoryRevision = 20;
    private const long AfterInventoryRevision = 21;
    private const long BeforeProgressionRevision = 30;
    private const long AfterProgressionRevision = 31;
    private static readonly Guid OperationId =
        Guid.Parse("ae47ee0d-2787-4fb0-b9a6-bba807b87b3b");
    private static readonly Guid EventId =
        Guid.Parse("97398d51-0a80-4510-b00f-039c8f00576f");
    private static readonly MethodInfo HandlePacketMethod =
        FindHandlerMethod("HandlePacketAsync");
    private static readonly MethodInfo InstallNpcCatalogMethod =
        FindHandlerMethod("InstallNpcCatalog");
    private static readonly RealmCalendar TestRealmCalendar =
        RealmCalendar.CreateForTesting(
            RealmId.Tempest,
            "Asia/Manila");

    private static readonly CompactItemEntry NameplateI =
        CompactItemEntry.Empty with
        {
            Id = FactionCrierRewardPolicy.FirstNameplateItemId,
            Quality = 1,
            Grade = 1,
            Stack = 1
        };

    private static readonly CompactItemEntry NameplateVI =
        NameplateI with
        {
            Id = FactionCrierRewardPolicy.LastNameplateItemId
        };

    private static async Task<FactionCrierFixture> CreateFixtureAsync(
        bool secure,
        CharacterAccountSnapshot before,
        CharacterAccountSnapshot after,
        FactionCrierExecutor executor,
        FactionCrierBalanceSnapshot? balance = null)
    {
        var hydrated = CharacterLoadSnapshotHydrator.Hydrate(before) ??
            throw new InvalidOperationException(
                "Faction Crier fixture did not hydrate.");
        var character = hydrated.Character;
        var npc = CreateFactionCrier(character);
        var worldContent = CreateWorldContent(npc);
        var secureTransport = secure
            ? new FactionCrierCaptureTransport()
            : null;
        var rawTransport = secure
            ? null
            : new ScriptedLegacyByteTransport(
                remoteEndPoint: "raw-local-faction-crier-check");
        ILegacyByteTransport transport = secure
            ? secureTransport!
            : rawTransport!;
        var session = new ClientSession(transport);
        var registry = GameHandlerOwnershipTestFences.CreateRegistry(
            session,
            before.AccountId,
            character);
        var snapshots = new FactionCrierSnapshotReader(after);
        var localAccess = secure
            ? null
            : LegacyAuthenticationAccess.Create(
                new ValidatedServerRuntimeProfile(
                    ServerRuntimeProfileKind.LocalDevelopment,
                    GameStorageProviderKind.Postgres,
                    ServerListenerTransport.RawTcp,
                    AllowsLegacyAuthentication: true));
        var handler = new GameClientHandler(
            session,
            new FactionCrierGameStore(),
            registry,
            snapshots,
            worldContent,
            legacyAuthenticationAccess: localAccess,
            factionCrierCommands: executor,
            factionCrierBalance: balance ??
                FactionCrierRewardPolicy.CreateReviewedDefault(),
            realmCalendar: TestRealmCalendar);
        SetHandlerField(
            handler,
            "_account",
            new AccountIdentity(AccountId, "faction-crier-handler-check"));
        SetHandlerField(handler, "_character", character);
        if (!secure)
        {
            SetHandlerField(handler, "_requiresDurablePlayerCommands", true);
        }

        var catalog = await registry.PublishMapNpcDefinitionsAsync(
            character.CurrentMap,
            [npc],
            originSession: null,
            CancellationToken.None);
        InstallNpcCatalogMethod.Invoke(handler, [catalog]);
        var visibility = GetHandlerField<WorldSectorVisibilityTracker<
            NpcSpawnDefinition>>(handler, "_npcVisibility") ??
            throw new InvalidOperationException(
                "Faction Crier visibility was not installed.");
        Check.True(
            visibility.TryCalculate(
                character.PositionX,
                character.PositionZ,
                out var delta),
            "Faction Crier visibility calculates");
        visibility.Commit(delta);

        return new FactionCrierFixture(
            session,
            secureTransport,
            rawTransport,
            handler,
            executor,
            snapshots,
            registry,
            character);
    }

    private static CharacterAccountSnapshot CreateSnapshot(
        int level,
        string kitBag,
        bool after)
    {
        var snapshot = CharacterSnapshotContractChecks.CreateValidSnapshot();
        var character = snapshot.Character ??
            throw new InvalidOperationException(
                "Faction Crier snapshot has no character.");
        var experience = after ? 2_000L : 1_000L;
        var talentPoints = after ? 30 : 9;
        return snapshot with
        {
            ProviderSnapshotToken = after
                ? "faction-crier-after"
                : "faction-crier-before",
            Character = character with
            {
                Identity = character.Identity with
                {
                    FactionCrierRevision = after
                        ? AfterFactionRevision
                        : BeforeFactionRevision
                },
                Progression = character.Progression with
                {
                    Level = level,
                    Experience = experience,
                    TalentPoints = talentPoints,
                    Revision = after
                        ? AfterProgressionRevision
                        : BeforeProgressionRevision
                },
                Wallet = new CharacterWalletSnapshot(
                    after ? 9_000 : 10_000,
                    after ? 395 : 500,
                    after ? 688 : 1_000),
                Loadout = character.Loadout with
                {
                    KitBag = kitBag,
                    InventoryRevision = after
                        ? AfterInventoryRevision
                        : BeforeInventoryRevision
                },
                CalculatedStats = character.CalculatedStats with
                {
                    Level = level
                }
            }
        };
    }

    private static string BagWithRenewalSource(bool target = false) =>
        KitBagSlots.SetSlot(
            GameDefaults.EmptyKitBag,
            RenewalSlot,
            (target ? NameplateVI : NameplateI).ToCompactString());

    private static string BagWithAllNameplates()
    {
        var bag = GameDefaults.EmptyKitBag;
        for (var index = 0;
             index < FactionCrierRewardPolicy.NameplateCount;
             index++)
        {
            bag = KitBagSlots.SetSlot(
                bag,
                index,
                (NameplateI with
                {
                    Id = checked((uint)(
                        FactionCrierRewardPolicy.FirstNameplateItemId +
                        index))
                }).ToCompactString());
        }
        return bag;
    }

    private static FactionCrierExecutionReceipt CreateReceipt(
        CharacterAccountSnapshot before,
        CharacterAccountSnapshot after,
        FactionCrierOperation operation,
        int subId,
        int nativeResultSubId,
        int awardedExperience = 0,
        int awardedTalentPoints = 0)
    {
        var previous = before.Character ??
            throw new InvalidOperationException("Missing before character.");
        var current = after.Character ??
            throw new InvalidOperationException("Missing after character.");
        return new FactionCrierExecutionReceipt(
            CharacterId,
            current.Identity.RealmId.Value,
            operation,
            subId,
            nativeResultSubId,
            Succeeded: true,
            previous.Progression.Level,
            current.Progression.Level,
            previous.Progression.Experience,
            current.Progression.Experience,
            awardedExperience,
            [],
            previous.Progression.TalentPoints,
            current.Progression.TalentPoints,
            awardedTalentPoints,
            current.Wallet,
            WalletRevision: 8,
            current.Loadout.InventoryRevision,
            current.Progression.Revision,
            current.Identity.FactionCrierRevision,
            "audit:faction-crier:handler-check",
            EventId);
    }

    private static NpcSpawnDefinition CreateFactionCrier(
        GameCharacter character) =>
        new(
            character.CurrentMap,
            "Athens",
            "Athens_055",
            "Athens_055_Male1",
            FactionCrierProtocol.AthensNpcId,
            character.PositionX,
            character.PositionZ,
            FactionCrierProtocol.AthensNpcId,
            AppearanceType: 1,
            Facing: 1.7f,
            Detail10077: [],
            Detail10080: []);

    private static IWorldContentReader CreateWorldContent(
        NpcSpawnDefinition npc) =>
        PinnedWorldContentReader.Create(
            "faction-crier-handler-v1",
            [npc.MapId],
            [npc],
            [],
            [],
            new DateTimeOffset(2026, 8, 21, 0, 0, 0, TimeSpan.Zero),
            npcTexts:
            [
                new NpcTextDefinition(
                    npc.NpcKey,
                    npc.SceneKey,
                    "Faction Crier",
                    "Faction Crier test dialogue")
            ],
            npcDialogueRoutes:
            [
                new NpcDialogueRouteDefinition(
                    npc.NpcKey,
                    npc.NpcKey,
                    FactionCrierProtocol.DialogIndex,
                    NpcDialogueBehavior.FactionCrier,
                    ImmutableArray.CreateRange(
                        FactionCrierProtocol.InitialMenuSubIds))
                {
                    RouteOrder = 0
                }
            ]);

    private static GamePacket CreateActionPacket(
        int subId,
        Action<int[]>? configure = null,
        Guid? operationId = null)
    {
        var arguments = Enumerable.Repeat(
            -1,
            FactionCrierProtocol.FunctionArgumentCount).ToArray();
        configure?.Invoke(arguments);
        var bytes = new byte[FactionCrierProtocol.ActionPacketBytes];
        BinaryPrimitives.WriteUInt16LittleEndian(
            bytes,
            checked((ushort)bytes.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(
            bytes.AsSpan(2),
            Opcodes.NpcFunctionAction);
        BinaryPrimitives.WriteUInt32LittleEndian(
            bytes.AsSpan(4),
            FactionCrierProtocol.AthensNpcId);
        BinaryPrimitives.WriteInt32LittleEndian(
            bytes.AsSpan(8),
            FactionCrierProtocol.DialogIndex);
        BinaryPrimitives.WriteInt32LittleEndian(
            bytes.AsSpan(12),
            FactionCrierProtocol.DialogIndex);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(16), subId);
        for (var index = 0; index < arguments.Length; index++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(
                bytes.AsSpan(20 + (index * sizeof(int))),
                arguments[index]);
        }
        return new GamePacket(bytes, operationId);
    }

    private static GamePacket CreateRenewalPacket(Guid? operationId) =>
        CreateActionPacket(
            4,
            arguments =>
            {
                arguments[0] = 46;
                arguments[FactionCrierProtocol.FirstItemArgumentIndex] =
                    RenewalCoordinate;
            },
            operationId);

    private static GamePacket CreateAllBoundGoldPacket(Guid? operationId) =>
        CreateActionPacket(
            2,
            arguments =>
            {
                arguments[0] = 20;
                arguments[1] = 109;
                arguments[2] = 131;
            },
            operationId);

    private static async Task InvokeAsync(
        GameClientHandler handler,
        GamePacket packet)
    {
        var task = HandlePacketMethod.Invoke(
            handler,
            [packet, CancellationToken.None]) as Task ??
            throw new InvalidOperationException(
                "Faction Crier handler did not return a task.");
        await task;
    }

    private static MethodInfo FindHandlerMethod(string name) =>
        typeof(GameClientHandler).GetMethod(
            name,
            BindingFlags.Instance | BindingFlags.NonPublic) ??
        throw new InvalidOperationException(
            $"GameClientHandler.{name} was not found.");

    private static void SetHandlerField<T>(
        GameClientHandler handler,
        string name,
        T value)
    {
        var field = typeof(GameClientHandler).GetField(
            name,
            BindingFlags.Instance | BindingFlags.NonPublic) ??
            throw new InvalidOperationException(
                $"GameClientHandler.{name} was not found.");
        field.SetValue(handler, value);
    }

    private static T? GetHandlerField<T>(
        GameClientHandler handler,
        string name)
    {
        var field = typeof(GameClientHandler).GetField(
            name,
            BindingFlags.Instance | BindingFlags.NonPublic) ??
            throw new InvalidOperationException(
                $"GameClientHandler.{name} was not found.");
        return (T?)field.GetValue(handler);
    }

    private sealed class FactionCrierExecutor :
        IFactionCrierCommandExecutor
    {
        public int ReplayCount { get; private set; }
        public int ExecuteCount { get; private set; }
        public FactionCrierReplayIntent? ReplayIntent { get; private set; }
        public FactionCrierOperationIdentity? ReplayIdentity { get; private set; }
        public CommandEnvelope<FactionCrierCommand>? Envelope { get; private set; }
        public FactionCrierExecutionResult ReplayResult { get; set; } =
            FactionCrierExecutionResult.ReplayNotFound();
        public FactionCrierExecutionResult? ExecuteResult { get; set; }

        public Task<FactionCrierExecutionResult> TryReplayAsync(
            CommandSubject subject,
            PlayerOwnershipFence ownership,
            FactionCrierReplayIntent replayIntent,
            FactionCrierOperationIdentity identity,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReplayCount++;
            ReplayIntent = replayIntent;
            ReplayIdentity = identity;
            Check.Equal(AccountId, subject.AccountId, "Faction Crier replay account");
            Check.Equal(CharacterId, subject.CharacterId, "Faction Crier replay character");
            return Task.FromResult(ReplayResult);
        }

        public Task<FactionCrierExecutionResult> ExecuteAsync(
            CommandEnvelope<FactionCrierCommand> envelope,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ExecuteCount++;
            Envelope = envelope;
            return Task.FromResult(
                ExecuteResult ?? throw new InvalidOperationException(
                    "Faction Crier execution was not configured."));
        }
    }

    private sealed class FactionCrierSnapshotReader(
        CharacterAccountSnapshot snapshot) : ICharacterSnapshotReader
    {
        public int ReadCount { get; private set; }

        public Task<CharacterAccountSnapshot> ReadAsync(
            int accountId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadCount++;
            Check.Equal(snapshot.AccountId, accountId, "Faction Crier projection account");
            return Task.FromResult(snapshot);
        }
    }

    private sealed class FactionCrierGameStore : GameStoreTestStub;

    private sealed record FactionCrierFixture(
        ClientSession Session,
        FactionCrierCaptureTransport? SecureTransport,
        ScriptedLegacyByteTransport? RawTransport,
        GameClientHandler Handler,
        FactionCrierExecutor Executor,
        FactionCrierSnapshotReader Snapshots,
        GameSessionRegistry Registry,
        GameCharacter Character) : IAsyncDisposable
    {
        public IReadOnlyList<byte[]> ReadLegacyPackets()
        {
            if (SecureTransport is not null)
            {
                return SecureTransport.ReadLegacyPackets();
            }

            var clear = RawTransport!.WrittenBytes;
            new PacketCipher().Transform(clear);
            var packets = new List<byte[]>();
            var offset = 0;
            while (offset < clear.Length)
            {
                var length = BinaryPrimitives.ReadUInt16LittleEndian(
                    clear.AsSpan(offset, sizeof(ushort)));
                if (length < 4 || length > clear.Length - offset)
                {
                    throw new InvalidDataException(
                        "Captured raw Faction Crier stream has an invalid frame.");
                }
                packets.Add(clear.AsSpan(offset, length).ToArray());
                offset += length;
            }
            return packets;
        }

        public async ValueTask DisposeAsync()
        {
            Registry.Remove(Session);
            await Session.DisposeAsync();
        }
    }
}
