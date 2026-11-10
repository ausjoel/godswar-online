using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Game.WorldInstances;
using Godswar.Server.Networking;
using Godswar.Server.State;
using Godswar.Server.World.Systems.Combat;

namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
    private bool TryResolveWonderlandSpawnLocked(ClientSession session, MonsterRuntimeSnapshot monster,
        DateTimeOffset now, out WorldInstanceRuntime runtime, out WonderlandSpawnPolicy policy)
    {
        runtime = null!;
        policy = null!;
        return _sessions.TryGetValue(session, out var context) && context.WorldReady && context.MapId == 207 &&
            IsCurrentAccountSession(context.AccountId, session, context.Ownership) &&
            TryGetWorldInstance(context, out runtime) &&
            TryGetWonderlandCombatForReadLocked(runtime, ref now, out var run, out _, allowCompleted: true) &&
            runtime.Map.TryGetWonderlandSpawnPolicy(monster.ObjectId, out policy) && IsWonderlandPublishedStage(run, policy.Stage) &&
            runtime.Map.TryGetMonsterSnapshot(monster.ObjectId, out var current) &&
            current.RuntimeInstanceId == monster.RuntimeInstanceId && current.SpawnGeneration == monster.SpawnGeneration;
    }

    private CombatTargetStats AdjustWonderlandMonsterTarget(ClientSession session, MonsterRuntimeSnapshot monster,
        DateTimeOffset now, CombatTargetStats original)
    {
        lock (_gate)
        {
            if (!TryResolveWonderlandSpawnLocked(session, monster, now, out _, out var policy)) return original;
            return original with
            {
                Level = policy.Stats.Level, PhysicalDefense = policy.Stats.PhysicalDefense,
                MagicDefense = policy.Stats.MagicDefense, Dodge = policy.Stats.Dodge,
                PhysicalDamageReductionBasisPoints = CombineWonderlandReduction(
                    original.PhysicalDamageReductionBasisPoints, policy.MechanicKey == "derskey" ? 8000 : 0),
                MagicDamageReductionBasisPoints = CombineWonderlandReduction(
                    original.MagicDamageReductionBasisPoints, policy.MechanicKey == "monkeyface" ? 8000 : 0),
                UsesDirectRatingAccuracy = policy.MechanicKey == "multihead"
            };
        }
    }

    private static int CombineWonderlandReduction(int existing, int encounter) =>
        10_000 - (10_000 - Math.Clamp(existing, 0, 10_000)) * (10_000 - encounter) / 10_000;

    private MonsterCombatProfile AdjustWonderlandMonsterProfile(ClientSession session, MonsterRuntimeSnapshot monster,
        DateTimeOffset now, MonsterCombatProfile original)
    {
        lock (_gate)
        {
            if (!TryResolveWonderlandSpawnLocked(session, monster, now, out _, out var policy)) return original;
            return WonderlandMonsterProfilePolicy.Resolve(original, policy);
        }
    }

    private CombatResolution ResolveWonderlandMonsterAttack(WorldInstanceRuntime runtime, MonsterRuntimeUpdate attack,
        GameSessionContext target, MonsterCombatProfile profile, RuntimeIncomingDamageMitigation mitigation,
        ulong eventId, DateTimeOffset now)
    {
        if (runtime.MapId != 207)
            return MonsterIncomingCombatPolicy.ResolveAttack(profile, target.Character, mitigation, eventId);
        var targetStats = MonsterIncomingCombatPolicy.ResolveTargetStats(target.Character, mitigation);
        if (TryGetWonderlandEffectsLocked(target.Session, ref now, out var effects) && effects.ArmorPenaltyUntil > now)
            targetStats = targetStats with
            {
                PhysicalDefense = checked((int)((long)targetStats.PhysicalDefense *
                    (10_000 - effects.ArmorPenaltyBasisPoints) / 10_000))
            };
        if (attack.WonderlandAbility is null &&
            runtime.Map.TryGetWonderlandSpawnPolicy(attack.Monster.ObjectId, out var spawn))
            profile = WonderlandCapturedAttackPolicy.ApplyProfile(profile, spawn, attack.Monster.ControlsAt(now));
        var attacker = profile.ToAttackerStats();
        if (attack.WonderlandAbility is { } ability)
            attacker = attacker with
            {
                Profession = ability.Magical ? (byte)3 : (byte)0,
                MagicAttack = attack.WonderlandGroundFire ? 22_000 : attacker.MagicAttack
            };
        var resolution = attack.WonderlandAbility is { } skill
            ? AuthoredCombatPveCurrent.ResolveSkillDamage(attacker, targetStats,
                skill.Magical ? 1 : 0, skill.DamageMultiplier - 1m, 0m, eventId)
            : AuthoredCombatPveCurrent.ResolveBasicAttack(attacker, targetStats, eventId);
        if (attack.WonderlandReflectedDamage is { } reflected)
            return resolution with { Damage = reflected, Outcome = CombatHitOutcome.Normal };
        return resolution;
    }

    private bool IsWonderlandIncomingAttackAllowedLocked(WorldInstanceRuntime runtime, MonsterRuntimeUpdate attack,
        GameSessionContext target, DateTimeOffset now)
    {
        if (runtime.MapId != 207) return true;
        if (!TryGetWonderlandCombatLocked(runtime, now, out var run, out _, allowCompleted: true) ||
            !runtime.Map.TryGetWonderlandSpawnPolicy(attack.Monster.ObjectId, out var policy) ||
            policy.IsAllied || !IsWonderlandPublishedStage(run, policy.Stage) ||
            !runtime.Map.TryGetMonsterSnapshot(attack.Monster.ObjectId, out var current) ||
            current.RuntimeInstanceId != attack.Monster.RuntimeInstanceId ||
            current.SpawnGeneration != attack.Monster.SpawnGeneration) return false;
        if (attack.WonderlandDeathBlast || attack.WonderlandReflectedDamage.HasValue)
            return attack.WonderlandAbility is not null;
        if (!current.IsAlive || !current.IsSpawned) return false;
        if (attack.WonderlandNativeAreaRadius > 0 &&
            DistanceSquared(attack.Monster.X, attack.Monster.Z,
                target.Character.PositionX, target.Character.PositionZ) >
            attack.WonderlandNativeAreaRadius * attack.WonderlandNativeAreaRadius) return false;
        // Terrain belongs to the island; controlling its visual source does
        // not extinguish a fire already placed on the ground.
        if (attack.WonderlandGroundFire) return true;
        if (attack.WonderlandAbility is not null)
            return current.CastInterruptionRevision == attack.Monster.CastInterruptionRevision &&
                (current.ControlsAt(now) & WonderlandSkillBlockingControls) == 0;
        var primary = WonderlandCapturedAttackPolicy.ResolvePrimarySkill(policy, HostileStatusControlFlags.None);
        return primary != 0 && (primary == 2000 ||
            current.CastInterruptionRevision == attack.Monster.CastInterruptionRevision);
    }

    private const HostileStatusControlFlags WonderlandSkillBlockingControls =
        HostileStatusControlFlags.NonMagicUsing | HostileStatusControlFlags.NonTechniqueUsing;
}
