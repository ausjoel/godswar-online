using Godswar.Server.Application.Characters;
using Godswar.Server.Application.Commands;
using Godswar.Server.Domain.World.Instances;

namespace Godswar.Server.Application.WorldInstances;

internal sealed record WonderlandBossLootClaimRequest(WorldInstanceId WorldInstanceId, RealmId RealmId,
    Guid ReservationId, DateTimeOffset StartedAt, DateTimeOffset DiedAt, uint BossObjectId,
    uint SpawnGeneration, Guid DeathEventId, int Island, byte PartyCamp, uint SackItemId,
    CommandSubject Subject, PlayerOwnershipFence Ownership)
{
    public bool IsValid => WorldInstanceId.IsValid && RealmId.IsValid && ReservationId != Guid.Empty &&
        StartedAt != default && DiedAt >= StartedAt && DiedAt < StartedAt + WonderlandEncounterPolicy.TimeLimit &&
        SpawnGeneration == 1 && DeathEventId != Guid.Empty && Subject.AccountId > 0 && Subject.CharacterId > 0 &&
        Ownership.IsValid && WonderlandBossLootPolicy.TryResolve(BossObjectId, PartyCamp, out var definition) &&
        definition.Island == Island && definition.SackItemId == SackItemId;
}
