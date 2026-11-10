using Godswar.Server.Application.Characters;
using Godswar.Server.Application.Coordination;
using Godswar.Server.Domain.Characters;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Game;
using Godswar.Server.Networking;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class BackhaulSkillHandlerChecks
{
    public const string TransitionCommitCheckName =
        "Faction backhaul committed relocation compensation";

    public static async Task RunTransitionCommitAsync()
    {
        const uint skillId = 3_062;
        Check.True(BackhaulSkillCatalog.TryGet(skillId, out var definition),
            "committed relocation fixture resolves Sparta portal");
        var transport = new FactionCrierCaptureTransport();
        await using var session = new ClientSession(transport);
        var character = CreateCharacter("CommittedPortalHero");
        var initialMana = character.CurrentMp;
        var store = new BackhaulStore(character,
            [new SkillState { SkillId = checked((int)skillId), Level = 1 }]);
        await using var registry = CreateRegistry();
        var ownership = GameHandlerOwnershipTestFences.Bind(
            registry, session, AccountId, character);
        registry.JoinMap(session, AccountId, character,
            WorldObjectIds.ForPlayer(CharacterId), worldReady: true, joinedAt: TestTime);
        var lease = new RejectEnteringAfterCommitLease(ownership, () =>
        {
            Check.True(character.CurrentMap == definition.TargetMapId &&
                character.PositionX == definition.TargetX &&
                character.PositionZ == definition.TargetZ &&
                registry.GetMapPopulation(PeloponneseMapId) == 0 &&
                registry.GetMapPopulation(definition.TargetMapId) == 1 &&
                store.PositionWrites is [var relocation] &&
                relocation.MapId == definition.TargetMapId,
                "coordination failpoint runs after durable and live relocation commit");
        });
        var handler = CreateEnteredHandler(session, store, registry, character,
            playerCoordination: new CommitFailureLeaseIssuer(lease));
        SetField(handler, "_playerCoordinationLease", lease);
        try
        {
            await InvokePacketAsync(handler, CreateSkillCastPacket(skillId,
                character.PositionX, character.PositionZ, 0f, 0f));
            await WaitForSameMapCastCompletionAsync(handler, transport,
                expectSceneChange: false);
            Check.True(lease.PublishAttempts == 1 && session.IsDisconnected,
                "postcommit coordination rejection requires a reconnect");
            Check.True(character.CurrentMap == definition.TargetMapId &&
                character.CurrentMp == initialMana - definition.ManaCost &&
                store.VitalsWrites is [var vitals] &&
                vitals.CurrentMp == initialMana - definition.ManaCost,
                "committed teleport retains its mana charge in memory and persistence");
            var packets = transport.ReadLegacyPackets();
            Check.True(!packets.Any(packet => ReadUInt16(packet, 2) == Opcodes.SceneChange) &&
                packets.Where(packet => ReadUInt16(packet, 2) == 0x2797)
                    .Select(packet => ReadInt32(packet, 8))
                    .SequenceEqual([initialMana - definition.ManaCost]),
                "postcommit failure emits neither a scene change nor a refund packet");
        }
        finally
        {
            await StopHandlerAsync(handler);
            registry.Remove(session);
        }
    }

    private sealed class CommitFailureLeaseIssuer(
        RejectEnteringAfterCommitLease lease) : IPlayerCoordinationLeaseIssuer
    {
        public bool IsEnabled => true;
        public ServerNodeId NodeId { get; } = new("commit-failure-worker");

        public bool TryResolveRoute(byte legacyMapId, out CoordinatedWorldRoute route)
        {
            route = new CoordinatedWorldRoute(RealmId.Tempest,
                MapId.FromLegacy(legacyMapId),
                new WorldInstanceId(new Guid(legacyMapId + 1, 0, 0, new byte[8])));
            return true;
        }

        public ValueTask<IPlayerCoordinationLease?> AcquireAsync(
            int accountId, int characterId, PlayerOwnershipFence ownership,
            CoordinatedWorldRoute route, Action ownershipLost,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IPlayerCoordinationLease?>(lease);
    }

    private sealed class RejectEnteringAfterCommitLease(
        PlayerOwnershipFence ownership, Action assertCommitted) : IPlayerCoordinationLease
    {
        public PlayerOwnershipFence Ownership { get; } = ownership;
        public Guid LeaseToken { get; } = Guid.NewGuid();
        public bool IsCurrent => true;
        public int PublishAttempts { get; private set; }

        public ValueTask<bool> PublishEnteringAsync(CoordinatedWorldRoute route,
            CancellationToken cancellationToken = default)
        {
            assertCommitted();
            PublishAttempts++;
            return ValueTask.FromResult(false);
        }

        public ValueTask<bool> PublishOnlineAsync(CoordinatedWorldRoute route,
            CancellationToken cancellationToken = default) => ValueTask.FromResult(true);
        public ValueTask<bool> ReleaseAsync() => ValueTask.FromResult(true);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
