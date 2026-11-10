using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Game.WorldInstances;
using Godswar.Server.Networking;
using Godswar.Server.Packets;
using Godswar.Server.State;
using Godswar.Server.World.Systems.Combat;

namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
    private readonly Dictionary<WorldInstanceId, WonderlandCombatState> _wonderlandCombat = [];

    private sealed class WonderlandCombatState
    {
        public int Stage;
        public DateTimeOffset LastAdvancedAt;
        public DateTimeOffset NextFireAt;
        public Dictionary<uint, WonderlandCaster> Casters { get; } = [];
        public Dictionary<ClientSession, WonderlandPlayerEffects> Players { get; } = [];
        public Dictionary<(Guid Runtime, uint ObjectId, uint Generation), ulong> DamageRevisions { get; } = [];
        public Queue<WonderlandScheduledCast> DeathBlasts { get; } = [];
        public List<WonderlandScheduledCast> GroundFires { get; } = [];
        public Dictionary<ulong, WonderlandNativeAreaBatch> NativeAreaHits { get; } = [];
        public Queue<WonderlandReflectedHit> Reflections { get; } = [];
        public Queue<WonderlandAlliedHit> AlliedHits { get; } = [];
        public Queue<Task> Notifications { get; } = [];
    }

    private sealed class WonderlandCaster
    {
        public Dictionary<string, DateTimeOffset> ReadyAt { get; } = [];
        public WonderlandScheduledCast? Pending;
    }

    private sealed class WonderlandPlayerEffects(GameSessionContext context, long life)
    {
        public GameSessionContext Context { get; } = context;
        public long LifeRevision { get; } = life;
        public DateTimeOffset AttackUntil;
        public DateTimeOffset HitUntil;
        public DateTimeOffset StunUntil = default;
        public DateTimeOffset SilenceUntil;
        public DateTimeOffset ArmorPenaltyUntil;
        public int ArmorPenaltyBasisPoints;
    }

    private sealed record WonderlandReflectedHit(GameSessionContext Target, long LifeRevision,
        MonsterRuntimeSnapshot Source, uint Damage, DateTimeOffset CommittedAt);

    private sealed record WonderlandScheduledCast(MonsterRuntimeSnapshot Source, WonderlandSpawnPolicy Policy,
        WonderlandBossAbility Ability, float X, float Z, float AimX, float AimZ,
        int? SelectedCharacterId, DateTimeOffset DueAt, bool DeathBlast = false, bool GroundFire = false);

    private bool TryGetWonderlandCombatLocked(WorldInstanceRuntime runtime, DateTimeOffset now,
        out WonderlandSnapshot snapshot, out WonderlandCombatState state, bool allowCompleted = true)
    {
        snapshot = null!;
        state = null!;
        if (runtime.MapId != 207 || !runtime.Map.TryGetWonderlandSnapshot(out snapshot) ||
            !WonderlandCompletionPolicy.IsCombatWindowOpen(snapshot, now) ||
            !allowCompleted && snapshot.State != WonderlandRunState.Active || now < snapshot.LastObservedAt ||
            _wonderlandCombat.TryGetValue(runtime.InstanceId, out var existing) && now < existing.LastAdvancedAt)
            return false;
        if (!_wonderlandCombat.TryGetValue(runtime.InstanceId, out state!))
        {
            state = new() { Stage = snapshot.ActiveStage, NextFireAt = now, LastAdvancedAt = now };
            _wonderlandCombat.Add(runtime.InstanceId, state);
        }
        if (state.Stage != snapshot.ActiveStage)
        {
            state.Stage = snapshot.ActiveStage;
            // Unlocking the next island does not stop fights on published islands.
            // Individual movement transitions clear the travelling player's effects.
            state.NextFireAt = now;
        }
        return now >= state.LastAdvancedAt;
    }

    // Read a delayed handler at the latest accepted world/encounter time. This
    // does not advance timers, create new casts, or resurrect expired effects.
    private bool TryGetWonderlandCombatForReadLocked(WorldInstanceRuntime runtime, ref DateTimeOffset now,
        out WonderlandSnapshot snapshot, out WonderlandCombatState state, bool allowCompleted = true)
    {
        snapshot = null!;
        state = null!;
        if (runtime.MapId != 207 || !runtime.Map.TryGetWonderlandSnapshot(out var observed) || now < observed.StartedAt)
            return false;
        if (now < observed.LastObservedAt) now = observed.LastObservedAt;
        if (_wonderlandCombat.TryGetValue(runtime.InstanceId, out var existing) && now < existing.LastAdvancedAt)
            now = existing.LastAdvancedAt;
        return TryGetWonderlandCombatLocked(runtime, now, out snapshot, out state, allowCompleted);
    }

    internal void ClearWonderlandCombat(WorldInstanceId instanceId)
    {
        lock (_gate) _wonderlandCombat.Remove(instanceId);
    }

    internal void ForgetWonderlandCombat(WorldInstanceId instanceId) => ClearWonderlandCombat(instanceId);

    private static bool IsWonderlandPublishedStage(WonderlandSnapshot run, int stage) =>
        stage >= 1 && stage <= run.ActiveStage && (stage < run.ActiveStage || !run.PublicationPending);

    internal void ClearWonderlandPlayerEffects(ClientSession session)
    {
        lock (_gate)
            foreach (var state in _wonderlandCombat.Values) state.Players.Remove(session);
    }

    private bool TryGetWonderlandEffectsLocked(ClientSession session, ref DateTimeOffset now,
        out WonderlandPlayerEffects effects)
    {
        effects = null!;
        if (!_sessions.TryGetValue(session, out var current) || !current.WorldReady || current.MapId != 207 ||
            !TryGetWorldInstance(current, out var runtime) ||
            !TryGetWonderlandCombatForReadLocked(runtime, ref now, out _, out var state) ||
            !state.Players.TryGetValue(session, out effects!) ||
            !IsCurrentWonderlandPlayerLocked(effects.Context, effects.LifeRevision)) return false;
        return true;
    }

    private bool IsCurrentWonderlandPlayerLocked(GameSessionContext expected, long life) =>
        _sessions.TryGetValue(expected.Session, out var current) && current.WorldReady &&
        current.WorldInstanceId == expected.WorldInstanceId && current.Ownership == expected.Ownership &&
        current.WorldMembershipEpoch == expected.WorldMembershipEpoch &&
        ReferenceEquals(current.Character, expected.Character) && current.Character.CurrentHp > 0 &&
        _playerLifeRevisions.TryGetValue(current.Session, out var actualLife) && actualLife == life &&
        IsCurrentAccountSession(current.AccountId, current.Session, current.Ownership);

    // ROUND2: every runtime status aggregate passes through here, so the GM's
    // own speed / attack / defence powers are layered on in the same place.
    private ClientStatusAggregate AdjustWonderlandPlayerModifiers(ClientSession session,
        DateTimeOffset now, ClientStatusAggregate aggregate) =>
        ApplyOperatorModifiers(
            session,
            AdjustWonderlandPlayerModifiersCore(session, now, aggregate));

    private ClientStatusAggregate AdjustWonderlandPlayerModifiersCore(ClientSession session,
        DateTimeOffset now, ClientStatusAggregate aggregate)
    {
        lock (_gate)
        {
            if (!TryGetWonderlandEffectsLocked(session, ref now, out var effects)) return aggregate;
            return aggregate with
            {
                Hit = checked((int)Math.Clamp((long)aggregate.Hit + (effects.HitUntil > now ? 25_000 : 0),
                    int.MinValue, int.MaxValue)),
                AttackMultiplier = effects.AttackUntil > now ? 5 : Math.Max(1, aggregate.AttackMultiplier)
            };
        }
    }

    private PlayerSkillCastControl GetWonderlandPlayerControl(ClientSession session, DateTimeOffset now)
    {
        lock (_gate)
        {
            if (!TryGetWonderlandEffectsLocked(session, ref now, out var effect)) return PlayerSkillCastControl.None;
            return effect.StunUntil > now ? PlayerSkillCastControl.Stunned :
                effect.SilenceUntil > now ? PlayerSkillCastControl.Silenced : PlayerSkillCastControl.None;
        }
    }

    internal bool IsWonderlandNonSpellActionAllowed(ClientSession session, DateTimeOffset now) =>
        GetWonderlandPlayerControl(session, now) != PlayerSkillCastControl.Stunned;

    private void CommitWonderlandIncomingEffectsLocked(WorldInstanceRuntime runtime, GameSessionContext target,
        MonsterRuntimeUpdate attack, CombatResolution resolution, uint appliedDamage, DateTimeOffset now)
    {
        if (!TryGetWonderlandCombatLocked(runtime, now, out var run, out var state) ||
            !runtime.Map.TryGetWonderlandSpawnPolicy(attack.Monster.ObjectId, out var policy) || policy.IsAllied ||
            !IsWonderlandPublishedStage(run, policy.Stage)) return;
        QueueWonderlandNativeAreaLocked(runtime, run, state, policy, attack, now);
        if (!_playerLifeRevisions.TryGetValue(target.Session, out var life) || target.Character.CurrentHp <= 0) return;
        if (!state.Players.TryGetValue(target.Session, out var effects) ||
            !IsCurrentWonderlandPlayerLocked(effects.Context, effects.LifeRevision))
            state.Players[target.Session] = effects = new(target, life);

        var changed = false;
        if (policy.MechanicKey == "petbird")
        {
            // A refresh inside the window must not re-log; the first hit that
            // grants the blessing is what an operator needs to see.
            var fresh = effects.AttackUntil <= now;
            effects.AttackUntil = now.AddSeconds(15);
            changed = true;
            if (fresh)
                Console.WriteLine($"[wonderland] petbird blessing character={target.Character.Name} " +
                    "physical-and-magic-attack=5x seconds=15");
        }
        if (policy.MechanicKey == "putridbird")
        {
            var fresh = effects.HitUntil <= now;
            effects.HitUntil = now.AddSeconds(15);
            changed = true;
            if (fresh)
                Console.WriteLine($"[wonderland] putridbird blessing character={target.Character.Name} " +
                    "hit=+25000 seconds=15");
        }
        if (policy.Stage == 4 && !policy.IsBoss && resolution.Hit && appliedDamage > 0 &&
            (attack.Monster.ControlsAt(now) & WonderlandSkillBlockingControls) == 0)
        {
            effects.SilenceUntil = now.AddSeconds(4);
            state.Notifications.Enqueue(RequestSkillCastInterruptionAsync(target.Session,
                SkillCastInterruptionReason.Silenced, CancellationToken.None));
            changed = true;
        }
        if (attack.WonderlandAbility is null && resolution.Hit && appliedDamage > 0 &&
            WonderlandCapturedAttackPolicy.ResolvePrimarySkill(policy, attack.Monster, now) == 2192)
        {
            effects.ArmorPenaltyBasisPoints = 1500;
            effects.ArmorPenaltyUntil = now.AddSeconds(20);
            changed = true;
        }
        if (resolution.Hit && appliedDamage > 0 && attack.WonderlandAbility is { PhysicalDefensePenaltyBasisPoints: > 0 } ability)
        {
            effects.ArmorPenaltyBasisPoints = ability.PhysicalDefensePenaltyBasisPoints;
            effects.ArmorPenaltyUntil = now.AddSeconds(ability.PenaltySeconds);
            changed = true;
        }
        if (changed) _wonderlandStatusSessions.TryAdd(target.Session, 0);
    }
}
