using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Reflection;
using Godswar.Server.Application.Accounts;
using Godswar.Server.Application.Characters;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Inventory;
using Godswar.Server.Application.OnlineAwards;
using Godswar.Server.Application.Realms;
using Godswar.Server.Application.World;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Game;
using Godswar.Server.Networking;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class OnlineAwardHandlerChecks
{
    private const int AccountId = 7;
    private const int CharacterId = 19;
    private const long BeforeAwardRevision = 10;
    private const long AfterAwardRevision = 11;
    private const long BeforeInventoryRevision = 20;
    private const long AfterInventoryRevision = 21;
    private static readonly Guid OperationId =
        Guid.Parse("a82146b7-2b9a-4540-a743-9fa6ba121125");
    private static readonly Guid EventId =
        Guid.Parse("145776ef-47ac-479a-891c-c88de2a03ebf");
    private static readonly MethodInfo HandlePacketMethod =
        FindHandlerMethod("HandlePacketAsync");
    private static readonly MethodInfo InstallNpcCatalogMethod =
        FindHandlerMethod("InstallNpcCatalog");
    private static readonly RealmCalendar TestCalendar =
        RealmCalendar.CreateForTesting(RealmId.Tempest, "Asia/Manila");

    private static readonly OnlineAwardBalanceSnapshot Balance = new(
        1,
        "A11516DCF5A5CAC3AAAC9CECEFF416C6510789F4386BBAF4C8D7CC9FB2B704FE",
        [
            new(0, 10150, 1, 14, 0, 1),
            new(1, 10150, 4, 10, 0, 1),
            new(2, 10134, 5, 1, 0, 99),
            new(3, 11005, 5, 1, 0, 99)
        ]);

    private static readonly CompactItemEntry Dew =
        CompactItemEntry.Empty with
        {
            Id = 10134,
            Quality = 1,
            Grade = 1,
            Bound = 0,
            Stack = 1
        };
    private static readonly CompactItemEntry Feather = Dew with
    {
        Id = 11005,
        Stack = 5
    };
    private static readonly CompactItemEntry GodlyEgg = Dew with
    {
        Id = 10150,
        Quality = 14
    };
    private static readonly CompactItemEntry SmartEgg = GodlyEgg with
    {
        Quality = 10
    };

    private static async Task<OnlineAwardFixture> CreateFixtureAsync(
        CharacterAccountSnapshot before,
        CharacterAccountSnapshot after,
        OnlineAwardExecutionResult result)
    {
        var hydrated = CharacterLoadSnapshotHydrator.Hydrate(before) ??
            throw new InvalidOperationException(
                "Online Award fixture did not hydrate.");
        var character = hydrated.Character;
        var npc = CreateNpc(character);
        var worldContent = CreateWorldContent(npc);
        var transport = new FactionCrierCaptureTransport();
        var session = new ClientSession(transport);
        var registry = GameHandlerOwnershipTestFences.CreateRegistry(
            session,
            before.AccountId,
            character);
        var snapshots = new OnlineAwardSnapshotReader(after);
        var executor = new OnlineAwardExecutor(result);
        var handler = new GameClientHandler(
            session,
            new OnlineAwardGameStore(),
            registry,
            snapshots,
            worldContent,
            onlineAwardCommands: executor,
            onlineAwardBalance: Balance,
            itemContent: TestItemContent.Content,
            realmCalendar: TestCalendar);
        SetHandlerField(
            handler,
            "_account",
            new AccountIdentity(AccountId, "online-award-handler-check"));
        SetHandlerField(handler, "_character", character);

        var catalog = await registry.PublishMapNpcDefinitionsAsync(
            character.CurrentMap,
            [npc],
            originSession: null,
            CancellationToken.None);
        InstallNpcCatalogMethod.Invoke(handler, [catalog]);
        var visibility = GetHandlerField<WorldSectorVisibilityTracker<
            NpcSpawnDefinition>>(handler, "_npcVisibility") ??
            throw new InvalidOperationException(
                "Online Award NPC visibility was not installed.");
        Check.True(
            visibility.TryCalculate(
                character.PositionX,
                character.PositionZ,
                out var delta),
            "Online Award NPC visibility calculates");
        visibility.Commit(delta);

        return new(
            session,
            transport,
            handler,
            executor,
            snapshots,
            registry,
            character);
    }

    private static CharacterAccountSnapshot CreateSnapshot(
        string kitBag,
        bool after)
    {
        var snapshot = CharacterSnapshotContractChecks.CreateValidSnapshot();
        var character = snapshot.Character ??
            throw new InvalidOperationException(
                "Online Award snapshot has no character.");
        return snapshot with
        {
            ProviderSnapshotToken = after
                ? "online-award-after"
                : "online-award-before",
            Character = character with
            {
                Identity = character.Identity with
                {
                    OnlineAwardRevision = after
                        ? AfterAwardRevision
                        : BeforeAwardRevision
                },
                Progression = character.Progression with
                {
                    Level = 80,
                    Experience = after ? 8_888 : 7_777,
                    TalentPoints = after ? 88 : 77
                },
                Wallet = new CharacterWalletSnapshot(
                    after ? 888 : 777,
                    after ? 88 : 77,
                    after ? 8 : 7),
                Loadout = character.Loadout with
                {
                    KitBag = kitBag,
                    InventoryRevision = after
                        ? AfterInventoryRevision
                        : BeforeInventoryRevision
                },
                CalculatedStats = character.CalculatedStats with
                {
                    Level = 80
                }
            }
        };
    }

    private static OnlineAwardExecutionReceipt CreateReceipt(
        long balanceRevision = 1,
        string? balanceSha256 = null,
        string? itemRevision = null) => new(
        CharacterId,
        RealmId.Tempest.Value,
        new DateOnly(2026, 8, 21),
        OnlineAwardProtocol.SuccessSubId,
        balanceRevision,
        balanceSha256 ?? Balance.Sha256,
        itemRevision ?? TestItemContent.Catalog.Revision.Sha256,
        Balance.Rewards.Select(static reward => new OnlineAwardItemDelta(
            reward.ItemId,
            reward.ItemQuality,
            reward.Bound,
            reward.Quantity)).ToArray(),
        AfterInventoryRevision,
        AfterAwardRevision,
        "7001",
        EventId);

    private static string BagBefore()
    {
        return KitBagSlots.SetSlot(
            GameDefaults.EmptyKitBag,
            0,
            Dew.ToCompactString());
    }

    private static string BagAfter()
    {
        var bag = KitBagSlots.SetSlot(
            GameDefaults.EmptyKitBag,
            0,
            (Dew with { Stack = 6 }).ToCompactString());
        bag = KitBagSlots.SetSlot(bag, 1, GodlyEgg.ToCompactString());
        for (var slot = 2; slot <= 5; slot++)
        {
            bag = KitBagSlots.SetSlot(
                bag,
                slot,
                SmartEgg.ToCompactString());
        }
        return KitBagSlots.SetSlot(bag, 6, Feather.ToCompactString());
    }

    private static NpcSpawnDefinition CreateNpc(GameCharacter character) =>
        new(
            character.CurrentMap,
            "Athens",
            "Athens_132",
            "Athens_132_Male1",
            OnlineAwardProtocol.AthensNpcId,
            character.PositionX,
            character.PositionZ,
            OnlineAwardProtocol.AthensNpcId,
            AppearanceType: 1,
            Facing: 1.7f,
            Detail10077: [],
            Detail10080: []);

    private static IWorldContentReader CreateWorldContent(
        NpcSpawnDefinition npc) => PinnedWorldContentReader.Create(
        "online-award-handler-v1",
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
                "Online Award Admin",
                "Online Award test dialogue")
        ],
        npcDialogueRoutes:
        [
            new NpcDialogueRouteDefinition(
                npc.NpcKey,
                npc.NpcKey,
                OnlineAwardProtocol.DialogIndex,
                NpcDialogueBehavior.OnlineAward,
                ImmutableArray.CreateRange(
                    OnlineAwardProtocol.InitialMenuSubIds))
            {
                RouteOrder = 0
            }
        ]);

    private static GamePacket CreateRequest(Guid operationId)
    {
        var bytes = new byte[92];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, 92);
        BinaryPrimitives.WriteUInt16LittleEndian(
            bytes.AsSpan(2),
            Opcodes.NpcFunctionAction);
        BinaryPrimitives.WriteUInt32LittleEndian(
            bytes.AsSpan(4),
            OnlineAwardProtocol.AthensNpcId);
        BinaryPrimitives.WriteInt32LittleEndian(
            bytes.AsSpan(8),
            OnlineAwardProtocol.DialogIndex);
        BinaryPrimitives.WriteInt32LittleEndian(
            bytes.AsSpan(12),
            OnlineAwardProtocol.DialogIndex);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(16), -1);
        for (var index = 0; index < 18; index++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(
                bytes.AsSpan(20 + (index * sizeof(int))),
                unchecked((int)(0xA5010000u + ((uint)index * 0x101u))));
        }
        return new GamePacket(bytes, operationId);
    }

    private static async Task InvokeAsync(GameClientHandler handler)
    {
        var task = HandlePacketMethod.Invoke(
            handler,
            [CreateRequest(OperationId), CancellationToken.None]) as Task ??
            throw new InvalidOperationException(
                "Online Award handler did not return a task.");
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

    private sealed class OnlineAwardExecutor(
        OnlineAwardExecutionResult result) : IOnlineAwardCommandExecutor
    {
        public int ExecuteCount { get; private set; }
        public CommandEnvelope<OnlineAwardCommand>? Envelope { get; private set; }

        public Task<OnlineAwardExecutionResult> ExecuteAsync(
            CommandEnvelope<OnlineAwardCommand> envelope,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ExecuteCount++;
            Envelope = envelope;
            return Task.FromResult(result);
        }
    }

    private sealed class OnlineAwardSnapshotReader(
        CharacterAccountSnapshot snapshot) : ICharacterSnapshotReader
    {
        public int ReadCount { get; private set; }

        public Task<CharacterAccountSnapshot> ReadAsync(
            int accountId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadCount++;
            Check.Equal(AccountId, accountId,
                "Online Award projection account");
            return Task.FromResult(snapshot);
        }
    }

    private sealed class OnlineAwardGameStore : GameStoreTestStub;

    private sealed record OnlineAwardFixture(
        ClientSession Session,
        FactionCrierCaptureTransport Transport,
        GameClientHandler Handler,
        OnlineAwardExecutor Executor,
        OnlineAwardSnapshotReader Snapshots,
        GameSessionRegistry Registry,
        GameCharacter Character) : IAsyncDisposable
    {
        public IReadOnlyList<byte[]> ReadPackets() =>
            Transport.ReadLegacyPackets();

        public async ValueTask DisposeAsync()
        {
            Registry.Remove(Session);
            await Session.DisposeAsync();
        }
    }
}
