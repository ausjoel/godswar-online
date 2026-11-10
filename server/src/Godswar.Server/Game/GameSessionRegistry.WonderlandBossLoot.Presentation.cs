using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Networking;
using Godswar.Server.Packets;

namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
    // The caller already holds the viewer's appearance-transition lease.
    internal async Task SendWonderlandBossCorpseAppearancesAsync(ClientSession session,
        IReadOnlyList<MonsterRuntimeSnapshot> entering, CancellationToken cancellationToken)
    {
        if (entering.Count == 0) return;
        var ids = entering.Where(monster => !monster.IsAlive && monster.IsSpawned)
            .Select(monster => (monster.ObjectId, monster.SpawnGeneration)).ToHashSet();
        foreach (var delivery in CaptureWonderlandBossLoot(session, DateTimeOffset.UtcNow, onlyPending: false)
                     .Where(delivery => ids.Contains((delivery.Corpse.Definition.MonsterObjectId,
                         delivery.Corpse.SpawnGeneration))))
            await SendWonderlandBossLootPresentationAsync(delivery, clearClaimed: false, cancellationToken);
    }

    internal async Task RefreshWonderlandBossLootAsync(ClientSession session, CancellationToken cancellationToken)
    {
        foreach (var delivery in CaptureWonderlandBossLoot(session, DateTimeOffset.UtcNow, onlyPending: false))
            await SendWonderlandBossLootWithLeaseAsync(delivery, clearClaimed: true, cancellationToken);
    }

    private async Task PublishPendingWonderlandBossLootAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        ClientSession[] sessions;
        lock (_gate) sessions = _sessions.Values.Where(actor => actor.MapId == WonderlandMapId)
            .Select(actor => actor.Session).ToArray();
        foreach (var session in sessions)
            foreach (var delivery in CaptureWonderlandBossLoot(session, now, onlyPending: true))
                try
                {
                    await SendWonderlandBossLootWithLeaseAsync(delivery, clearClaimed: false, cancellationToken);
                }
                catch (Exception error) when (error is IOException or ObjectDisposedException)
                {
                    Remove(session);
                }
    }

    private WonderlandBossLootDelivery[] CaptureWonderlandBossLoot(ClientSession session,
        DateTimeOffset now, bool onlyPending)
    {
        lock (_gate)
        {
            if (!TryGetWonderlandBossLootActorLocked(session, now, out var actor, out var run) ||
                !WorldInstances.TryFind(actor.WorldInstanceId, out var runtime)) return [];
            return InvokeWorldOwner(runtime, static map => map.SnapshotWonderlandBossCorpses())
                .Where(corpse => WonderlandBossLootPolicy.IsCorpseClaimWindowOpen(run, corpse.DiedAt, now))
                .Where(corpse => !onlyPending || !_wonderlandBossLootPresented.Contains((actor.WorldInstanceId,
                    corpse.Definition.MonsterObjectId, corpse.SpawnGeneration, session)))
                .Select(corpse => new WonderlandBossLootDelivery(actor, corpse)).ToArray();
        }
    }

    private async Task SendWonderlandBossLootWithLeaseAsync(WonderlandBossLootDelivery delivery,
        bool clearClaimed, CancellationToken cancellationToken)
    {
        if (!WorldInstances.TryFind(delivery.Actor.WorldInstanceId, out var runtime)) return;
        await using var lease = await runtime.Map.AcquireMonsterViewerDeliveryLeaseAsync(delivery.Actor.Session,
            delivery.Corpse.Definition.MonsterObjectId, delivery.Corpse.SpawnGeneration, cancellationToken);
        if (lease is null) return;
        await SendWonderlandBossLootPresentationAsync(delivery, clearClaimed, cancellationToken);
        lease.Commit();
    }

    private async Task SendWonderlandBossLootPresentationAsync(WonderlandBossLootDelivery delivery,
        bool clearClaimed, CancellationToken cancellationToken)
    {
        var actor = delivery.Actor;
        var corpse = delivery.Corpse;
        var objectId = corpse.Definition.MonsterObjectId;
        bool claimed;
        lock (_gate)
        {
            if (!IsCurrentWonderlandMember(actor, actor.WorldInstanceId) ||
                !TryGetWonderlandEncounterSnapshot(actor.WorldInstanceId, out var currentRun) ||
                !WonderlandBossLootPolicy.IsCorpseClaimWindowOpen(currentRun, corpse.DiedAt, DateTimeOffset.UtcNow)) return;
            claimed = _wonderlandBossLootClaimed.Contains((actor.WorldInstanceId, objectId,
                corpse.SpawnGeneration, actor.CharacterId));
        }
        // Death is already represented by the committed damage/zero-HP appearance.
        // Native 10027 is loot cleanup, not the monster's death-pose instruction.
        if (!claimed)
            await actor.Session.SendAsync(PacketBuilder.MonsterLoot(objectId,
                    [new MonsterLootEntry(0, 0, corpse.Definition.SackItemId, 1)]),
                cancellationToken, "WonderlandBossCorpseLoot");
        else if (clearClaimed)
        {
            // Native local-player ACK can fail when its bag is full. Establish
            // exactly one slot, then take the loot-only observer ACK branch.
            // Repeating this pair cannot underflow the native count or add items.
            await actor.Session.SendAsync(PacketBuilder.MonsterLoot(objectId,
                    [new MonsterLootEntry(0, 0, corpse.Definition.SackItemId, 1)]),
                cancellationToken, "WonderlandBossCorpseClaimedSlot");
            await actor.Session.SendAsync(PacketBuilder.WonderlandBossLootPickupAck(0, objectId, 0),
                cancellationToken, "WonderlandBossCorpseLootCleared");
        }
        lock (_gate)
            _wonderlandBossLootPresented.Add((actor.WorldInstanceId, objectId, corpse.SpawnGeneration, actor.Session));
    }

    private sealed record WonderlandBossLootDelivery(GameSessionContext Actor, WonderlandBossCorpse Corpse);

    internal static async Task ClearExpiredWonderlandCorpseLootAsync(ClientSession session, MapInstance map,
        uint objectId, CancellationToken token)
    {
        if (map.MapId != 207 || !map.TryGetWonderlandBossCorpse(objectId, out _)) return;
        // Capture10027 occurs20 seconds after drops, not at death. Clear native
        // loot state immediately before the actual scene-object removal.
        await session.SendAsync(PacketBuilder.MonsterDeathReward(objectId, uint.MaxValue, 0, 0, 0),
            token, "WonderlandBossCorpseLootExpired");
    }
}
