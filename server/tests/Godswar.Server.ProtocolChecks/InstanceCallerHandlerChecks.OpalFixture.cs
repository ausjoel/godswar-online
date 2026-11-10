using System.Collections.Immutable;
using System.Reflection;
using Godswar.Server.Application.Accounts;
using Godswar.Server.Application.Characters;
using Godswar.Server.Application.World;
using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Game;
using Godswar.Server.Networking;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class InstanceCallerHandlerChecks
{
    private const int AtlantisOpalSlot = 7;

    private static readonly MethodInfo
        OpalAuthoritativeInstanceTransitionMethod =
            FindHandlerMethod(
                "HandleAuthoritativeInstanceTransitionAsync");

    private static async Task<AtlantisOpalFixture>
        CreateAtlantisOpalFixtureAsync(
            ScriptedLegacyInstanceDailyEntryStore? dailyEntries,
            ScriptedLegacyInstanceOpalPaymentStore? opalPayments,
            IReadOnlySet<int>? failedFollowerIndexes = null,
            int partySize = 3,
            bool joinParty = true)
    {
        if (partySize is < 1 or > 5)
        {
            throw new ArgumentOutOfRangeException(nameof(partySize));
        }

        var leader = await CreateFixtureAsync(
            level: 90,
            transitionReady: true,
            legacyInstanceDailyEntries: dailyEntries,
            legacyInstanceOpalPayments: opalPayments);
        var followers = new List<AtlantisOpalFollower>(partySize - 1);
        try
        {
            for (var index = 1; index < partySize; index++)
            {
                var snapshot =
                    CharacterSnapshotContractChecks.CreateValidSnapshot();
                var hydrated = CharacterLoadSnapshotHydrator.Hydrate(
                    snapshot) ?? throw new InvalidOperationException(
                        "Atlantis Opal follower did not hydrate.");
                var character = hydrated.Character;
                character.Id = leader.Character.Id + index;
                character.AccountId = leader.Character.AccountId + index;
                character.Name = $"AtlantisOpalMember{index}";
                character.Level = 90;
                character.PositionX = leader.Character.PositionX;
                character.PositionZ = leader.Character.PositionZ;

                var transport = new FactionCrierCaptureTransport();
                var session = new ClientSession(transport);
                GameHandlerOwnershipTestFences.Bind(
                    leader.Registry,
                    session,
                    character.AccountId,
                    character);
                leader.Registry.JoinMap(
                    session,
                    character.AccountId,
                    character,
                    objectId: checked((uint)(700_030 + index)));

                var npc = new NpcSpawnDefinition(
                    character.CurrentMap,
                    "Athens",
                    "Athens_060",
                    "Athens_060_FemMale17",
                    InstanceCallerProtocol.AthensNpcId,
                    character.PositionX,
                    character.PositionZ,
                    InstanceCallerProtocol.AthensNpcId,
                    AppearanceType: 1,
                    Facing: 1.7f,
                    Detail10077: [],
                    Detail10080: []);
                var route = new NpcDialogueRouteDefinition(
                    npc.NpcKey,
                    npc.NpcKey,
                    InstanceCallerProtocol.DialogIndex,
                    NpcDialogueBehavior.InstanceCaller,
                    ImmutableArray.CreateRange(
                        InstanceCallerProtocol.InitialMenuSubIds));
                var worldContent = PinnedWorldContentReader.Create(
                    $"atlantis-opal-member-{index}",
                    [
                        (short)character.CurrentMap,
                        205,
                        DynamicDungeonContentMapPolicy.HarborAttackFirstMapId,
                        DynamicDungeonContentMapPolicy.HarborAttackSecondMapId
                    ],
                    [npc],
                    [],
                    [],
                    new DateTimeOffset(
                        2026,
                        9,
                        1,
                        0,
                        0,
                        0,
                        TimeSpan.Zero),
                    npcTexts:
                    [
                        new NpcTextDefinition(
                            npc.NpcKey,
                            npc.SceneKey,
                            "Instance Caller",
                            "Atlantis Opal consent test dialogue")
                    ],
                    npcDialogueRoutes: [route]);
                var handler = new GameClientHandler(
                    session,
                    new InstanceCallerGameStore(),
                    leader.Registry,
                    CharacterSnapshotReaderTestFixtures.Unused,
                    worldContent,
                    // Production injects the daily-entry store into every
                    // handler; a member who claims an entry on their own behalf
                    // must reach the same store the leader's claim does.
                    legacyInstanceDailyEntries: dailyEntries);
                SetHandlerField(
                    handler,
                    "_account",
                    new AccountIdentity(
                        character.AccountId,
                        $"atlantis-opal-member-{index}"));
                SetHandlerField(handler, "_character", character);
                SetHandlerField(handler, "_registered", true);
                SetHandlerField(
                    handler,
                    "_worldPresenceAnnounced",
                    true);

                var catalog = await leader.Registry
                    .PublishMapNpcDefinitionsAsync(
                        character.CurrentMap,
                        [npc],
                        originSession: null,
                        CancellationToken.None);
                InstallNpcCatalogMethod.Invoke(handler, [catalog]);
                var visibility = GetHandlerField<
                    WorldSectorVisibilityTracker<NpcSpawnDefinition>>(
                        handler,
                        "_npcVisibility") ??
                    throw new InvalidOperationException(
                        "Atlantis consent NPC visibility was not installed.");
                Check.True(
                    visibility.TryCalculate(
                        character.PositionX,
                        character.PositionZ,
                        out var visibilityDelta),
                    "Atlantis consent NPC visibility calculates");
                visibility.Commit(visibilityDelta);

                var shouldFail =
                    failedFollowerIndexes?.Contains(index) == true;
                leader.Registry.RegisterAuthoritativeInstanceTransitionSink(
                    session,
                    shouldFail
                        ? static (_, cancellationToken) =>
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            return Task.FromResult(false);
                        }
                        : (command, cancellationToken) =>
                            InvokeAuthoritativeTransitionAsync(
                                handler,
                                command,
                                cancellationToken));
                followers.Add(new(
                    character,
                    session,
                    transport,
                    handler));

                var now = DateTimeOffset.UtcNow.AddSeconds(index * 2);
                if (!joinParty)
                {
                    // A follower who is online in the same realm but in no party:
                    // what an instance's own "invite by name" picks up.
                    continue;
                }
                var invited = leader.Registry.InvitePartyMember(
                    leader.Session,
                    leader.Character.Name,
                    character.Name,
                    now);
                var accepted = leader.Registry.AcceptPartyInvite(
                    session,
                    leader.Character.Name,
                    character.Name,
                    now.AddSeconds(1));
                Check.True(
                    invited.Status == PartyOperationStatus.Applied &&
                    accepted.Status == PartyOperationStatus.Applied,
                    $"Atlantis Opal follower {index} joins the party");
            }

            return new AtlantisOpalFixture(leader, followers);
        }
        catch
        {
            foreach (var follower in followers)
            {
                leader.Registry.UnregisterAuthoritativeInstanceTransitionSink(
                    follower.Session);
                leader.Registry.Remove(follower.Session);
                await follower.Session.DisposeAsync();
            }
            await leader.DisposeAsync();
            throw;
        }
    }

    private static async Task<bool> InvokeAuthoritativeTransitionAsync(
        GameClientHandler handler,
        AuthoritativeInstanceTransitionCommand command,
        CancellationToken cancellationToken)
    {
        var task = OpalAuthoritativeInstanceTransitionMethod.Invoke(
            handler,
            [command, cancellationToken]) as Task<bool> ??
            throw new InvalidOperationException(
                "Authoritative instance transition did not return a task.");
        return await task;
    }

    private static async Task OpenAtlantisPageAsync(
        InstanceCallerFixture leader)
    {
        var before = leader.ReadPackets().Count;
        await InvokeAsync(
            leader.Handler,
            CreateActionPacket(InstanceCallerProtocol.AtlantisRootSubId));
        Check.True(
            leader.ReadPackets().Skip(before).Single().SequenceEqual(
                Godswar.Server.Packets.PacketBuilder
                    .NpcFunctionActionResponse(
                        InstanceCallerProtocol.AthensNpcId,
                        InstanceCallerProtocol.DialogIndex,
                        InstanceCallerProtocol.AtlantisPageSubIds.ToArray())),
            "Atlantis Opal test opens a fresh proved destination page");
    }

    private static void InstallOpals(
        AtlantisOpalFixture fixture,
        short stack = 2)
    {
        foreach (var character in fixture.Characters)
        {
            character.KitBag = KitBagSlots.SetSlot(
                character.KitBag,
                AtlantisOpalSlot,
                (CompactItemEntry.Empty with
                {
                    Id = LegacyInstanceOpalPaymentPolicy
                        .OpalItemTemplateId,
                    Quality = 1,
                    Grade = 1,
                    Stack = stack
                }).ToCompactString());
        }
    }

    private sealed record AtlantisOpalFollower(
        GameCharacter Character,
        ClientSession Session,
        FactionCrierCaptureTransport Transport,
        GameClientHandler Handler);

    private sealed record AtlantisOpalFixture(
        InstanceCallerFixture Leader,
        IReadOnlyList<AtlantisOpalFollower> Followers) : IAsyncDisposable
    {
        public IReadOnlyList<GameCharacter> Characters =>
            [Leader.Character, .. Followers.Select(x => x.Character)];

        public IReadOnlyList<ClientSession> Sessions =>
            [Leader.Session, .. Followers.Select(x => x.Session)];

        public IReadOnlyList<IReadOnlyList<byte[]>> ReadAllPackets() =>
        [
            Leader.ReadPackets(),
            .. Followers.Select(follower =>
                follower.Transport.ReadLegacyPackets())
        ];

        public async ValueTask DisposeAsync()
        {
            foreach (var follower in Followers.Reverse())
            {
                Leader.Registry
                    .UnregisterAuthoritativeInstanceTransitionSink(
                        follower.Session);
                Leader.Registry.Remove(follower.Session);
                await follower.Session.DisposeAsync();
            }
            await Leader.DisposeAsync();
        }
    }
}
