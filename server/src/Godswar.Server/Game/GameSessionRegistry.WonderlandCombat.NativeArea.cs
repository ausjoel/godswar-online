using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Game.WorldInstances;

namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
    private sealed record WonderlandNativeAreaBatch(DateTimeOffset CommittedAt, MonsterRuntimeUpdate[] Hits);
    internal Action? WonderlandNativeAreaDrainingHook { get; set; }

    // The solo capture pins one target's native sequence. The matching client
    // definitions supply area radii; applying those to other participants is
    // an explicit reconstruction, not an observed external party damage trace.
    private void QueueWonderlandNativeAreaLocked(WorldInstanceRuntime runtime, WonderlandSnapshot run,
        WonderlandCombatState state, WonderlandSpawnPolicy policy, MonsterRuntimeUpdate primary, DateTimeOffset now)
    {
        if (primary.WonderlandAbility is not null || primary.WonderlandNativeAreaRadius > 0 || primary.AttackEventId == 0) return;
        var radius = WonderlandCapturedAttackPolicy.NativeAreaRadius(policy, primary.Monster, now);
        if (radius <= 0) return;
        var hits = new List<MonsterRuntimeUpdate>();
        foreach (var candidate in _sessions.Values)
        {
            if (!candidate.WorldReady || candidate.WorldInstanceId != runtime.InstanceId ||
                candidate.CharacterId == primary.TargetCharacterId || candidate.Character.CurrentHp <= 0 ||
                !run.Participants.Any(member => member.CharacterId == candidate.CharacterId) ||
                !_playerLifeRevisions.TryGetValue(candidate.Session, out var life) ||
                !IsCurrentWonderlandPlayerLocked(candidate, life) ||
                !runtime.Map.IsMonsterVisibleTo(candidate.Session, primary.Monster.ObjectId) ||
                !IsWonderlandCombatPosition(policy.Stage, candidate.Character.PositionX, candidate.Character.PositionZ) ||
                DistanceSquared(primary.Monster.X, primary.Monster.Z,
                    candidate.Character.PositionX, candidate.Character.PositionZ) > radius * radius) continue;
            hits.Add(primary with
            {
                TargetCharacterId = candidate.CharacterId, TargetObjectId = candidate.ObjectId,
                TargetX = candidate.Character.PositionX, TargetZ = candidate.Character.PositionZ,
                TargetLifeRevision = life, TargetVitalsRevision = candidate.Character.VitalsRevision,
                TargetOwnership = candidate.Ownership, TargetWorldInstanceId = candidate.WorldInstanceId,
                TargetWorldRevision = candidate.WorldRevision, TargetWorldMembershipEpoch = candidate.WorldMembershipEpoch,
                AttackEventId = AllocateRequiredMonsterAttackEventIdAbove(0), WonderlandNativeAreaRadius = radius
            });
        }
        if (hits.Count > 0) state.NativeAreaHits.Add(primary.AttackEventId, new(now, hits.ToArray()));
    }

    private async Task ProcessMonsterAttackAsync(WorldInstanceRuntime runtime, MonsterRuntimeUpdate attack,
        CancellationToken cancellationToken, DateTimeOffset? capturedWorldTime = null)
    {
        // Live legacy/ECS ticks leave the optional ID at zero. Resolve it from
        // the ORIGINAL update identity before cloning so replaying that same
        // emitted update retains its event, including its one-shot area batch.
        if (runtime.MapId == 207 && attack.AttackEventId == 0)
            attack = attack with { AttackEventId = ResolveMonsterAttackEventId(attack) };
        try
        {
            await ProcessMonsterAttackCoreAsync(runtime, attack, cancellationToken, capturedWorldTime);
        }
        finally
        {
            // Queue insertion occurs only after the primary's irreversible
            // attack commit, including a miss or lethal hit. Replays cannot
            // allocate another batch. Admission never awaits under _gate.
            WonderlandNativeAreaBatch? batch = null;
            if (runtime.MapId == 207 && attack.WonderlandNativeAreaRadius == 0)
                WonderlandNativeAreaDrainingHook?.Invoke();
            if (runtime.MapId == 207 && attack.WonderlandNativeAreaRadius == 0)
                lock (_gate)
                    if (_wonderlandCombat.TryGetValue(runtime.InstanceId, out var state))
                        state.NativeAreaHits.Remove(attack.AttackEventId, out batch);
            if (batch is not null)
                foreach (var secondary in batch.Hits)
                    try
                    {
                        // Persistence/egress for the primary may have yielded
                        // across a newer world tick. Admit each secondary at
                        // the latest encounter time, retaining its exact actor
                        // and target authority; an old tick alone is no miss.
                        var admittedAt = DateTimeOffset.UtcNow;
                        if (admittedAt < batch.CommittedAt) admittedAt = batch.CommittedAt;
                        lock (_gate)
                            if (!TryResolveWonderlandDamageTimeLocked(runtime, secondary.Monster.ObjectId, ref admittedAt)) continue;
                        await ProcessMonsterAttackCoreAsync(runtime, secondary, CancellationToken.None, admittedAt);
                    }
                    catch (MonsterAttackTargetUnavailableException)
                    {
                        // Departure, death, movement or a new control cancels
                        // only this exact participant's pending area hit.
                    }
        }
    }
}
