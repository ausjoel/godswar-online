using Godswar.Server.Application.Commands;
using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Networking;

namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
    private readonly HashSet<(WorldInstanceId Instance, uint Object, uint Generation, int Character)>
        _wonderlandBossLootClaimed = [];
    private readonly HashSet<(WorldInstanceId Instance, uint Object, uint Generation, ClientSession Session)>
        _wonderlandBossLootPresented = [];

    internal bool TryResolveWonderlandBossLootPickup(ClientSession session, uint objectId, int pickupIndex,
        DateTimeOffset now, out WonderlandBossLootClaimRequest request)
    {
        request = null!;
        lock (_gate)
        {
            if (pickupIndex != 0 || !TryGetWonderlandBossLootActorLocked(session, now, out var actor, out var run) ||
                actor.Character.CurrentHp <= 0 || !WorldInstances.TryFind(actor.WorldInstanceId, out var runtime)) return false;
            var corpse = InvokeWorldOwner(runtime, map => map.TryGetWonderlandBossCorpse(objectId, out var value)
                ? value : null);
            if (corpse is null || !WonderlandBossLootPolicy.IsCorpseClaimWindowOpen(run, corpse.DiedAt, now) ||
                _wonderlandBossLootClaimed.Contains((actor.WorldInstanceId, objectId,
                    corpse.SpawnGeneration, actor.CharacterId))) return false;
            var dx = (double)actor.Character.PositionX - corpse.X;
            var dz = (double)actor.Character.PositionZ - corpse.Z;
            if (!double.IsFinite(dx) || !double.IsFinite(dz) || dx * dx + dz * dz >
                WonderlandBossLootPolicy.InteractionRadius * WonderlandBossLootPolicy.InteractionRadius) return false;
            var admission = _wonderlandAdmissions[actor.WorldInstanceId];
            request = new(actor.WorldInstanceId, actor.RealmId, admission.ReservationId, admission.StartedAt,
                corpse.DiedAt, objectId, corpse.SpawnGeneration, corpse.DeathEventId,
                corpse.Definition.Island, run.PartyCamp, corpse.Definition.SackItemId,
                new CommandSubject(actor.AccountId, actor.CharacterId), actor.Ownership);
            return request.IsValid;
        }
    }

    internal void MarkWonderlandBossLootClaimed(ClientSession session, WorldInstanceId instanceId,
        uint objectId, uint generation)
    {
        lock (_gate)
            if (_sessions.TryGetValue(session, out var actor) && actor.WorldInstanceId == instanceId)
                _wonderlandBossLootClaimed.Add((instanceId, objectId, generation, actor.CharacterId));
    }

    private bool TryGetWonderlandBossLootActorLocked(ClientSession session, DateTimeOffset now,
        out GameSessionContext actor, out WonderlandSnapshot run)
    {
        run = null!;
        return _sessions.TryGetValue(session, out actor!) &&
            IsCurrentWonderlandMember(actor, actor.WorldInstanceId) &&
            _wonderlandAdmissions.TryGetValue(actor.WorldInstanceId, out var admission) && admission.Sealed &&
            admission.Entrants.Contains(actor.CharacterId) &&
            TryGetWonderlandEncounterSnapshot(actor.WorldInstanceId, out run) &&
            WonderlandBossLootPolicy.IsClaimWindowOpen(run, now);
    }

    private void ForgetWonderlandBossLoot(WorldInstanceId instanceId)
    {
        lock (_gate)
        {
            _wonderlandBossLootClaimed.RemoveWhere(key => key.Instance == instanceId);
            _wonderlandBossLootPresented.RemoveWhere(key => key.Instance == instanceId);
        }
    }
}
