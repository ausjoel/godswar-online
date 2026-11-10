using Godswar.Server.Game.WorldInstances;
using Godswar.Server.Networking;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
    internal bool TryCommitPlayerMonsterControl(ClientSession session, MonsterRuntimeSnapshot target,
        PlayerMonsterCombatAuthority expectedAuthority, HostileStatusEffectDefinition definition,
        DateTimeOffset now, out MonsterControlResult result)
    {
        result = null!;
        if (target.Definition.MapId is < byte.MinValue or > byte.MaxValue) return false;
        lock (_gate)
        {
            if (!TryResolvePlayerMonsterAuthorityLocked(session, checked((byte)target.Definition.MapId),
                    out var context, out var runtime, out var authority) ||
                authority != expectedAuthority || context.Character.CurrentHp <= 0 ||
                context.Character.Profession != definition.RequiredProfession ||
                !IsWonderlandPlayerDamageAllowed(runtime, target.ObjectId) ||
                !TryResolveWonderlandDamageTimeLocked(runtime, target.ObjectId, ref now)) return false;
            result = InvokeWorldOwnerAuthoritativeMutation(runtime, map =>
                map.TryCommitMonsterControl(session, target, expectedAuthority, definition, now))!;
            if (result is { Applied: true }) ScheduleMonsterControlExpiryLocked(runtime, result.Monster, now);
            return result is { Applied: true };
        }
    }

    // Called under the registry gate, before any target vitals lock. A control
    // landing while a previously emitted attack awaited dispatch cancels it,
    // even if that control has expired by the time the queued attack resumes.
    private bool IsMonsterBasicAttackControlAllowedLocked(WorldInstanceRuntime runtime,
        MonsterRuntimeUpdate attack, DateTimeOffset now)
    {
        if (attack.WonderlandAbility is not null || attack.WonderlandDeathBlast ||
            attack.WonderlandGroundFire || attack.WonderlandReflectedDamage.HasValue) return true;
        return InvokeWorldOwner(runtime, map => map.TryGetMonsterSnapshot(attack.Monster.ObjectId, out var current) &&
            current.RuntimeInstanceId == attack.Monster.RuntimeInstanceId &&
            current.SpawnGeneration == attack.Monster.SpawnGeneration && current.IsAlive && current.IsSpawned &&
            current.CombatPhase is not (MonsterCombatPhase.Returning or MonsterCombatPhase.AwaitingRetirement) &&
            current.AttackInterruptionRevision == attack.Monster.AttackInterruptionRevision &&
            !current.ControlsAt(now).HasFlag(HostileStatusControlFlags.NonAttackUsing));
    }
}
