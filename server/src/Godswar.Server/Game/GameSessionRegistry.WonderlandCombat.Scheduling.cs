using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Game.WorldInstances;
using Godswar.Server.Packets;
using Godswar.Server.World.Systems.Combat;

namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
    internal async Task AdvanceWonderlandCombatAsync(WorldInstanceRuntime runtime, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (runtime.MapId != 207) return;
        var due = new List<WonderlandScheduledCast>();
        var reflections = new List<WonderlandReflectedHit>();
        var alliedHits = new List<WonderlandAlliedHit>();
        var notifications = new List<Task>();
        lock (_gate)
        {
            if (!runtime.Map.TryGetWonderlandSnapshot(out var observed) ||
                observed.State is WonderlandRunState.Cancelled or WonderlandRunState.TimedOut)
            {
                _wonderlandCombat.Remove(runtime.InstanceId);
                return;
            }
            // An older tick must not erase buffs or already committed corpse
            // blasts installed by a later accepted combat event.
            if (now < observed.LastObservedAt || _wonderlandCombat.TryGetValue(runtime.InstanceId, out var previous) &&
                now < previous.LastAdvancedAt) return;
            if (!TryGetWonderlandCombatLocked(runtime, now, out var run, out var state, allowCompleted: true))
            {
                _wonderlandCombat.Remove(runtime.InstanceId);
                return;
            }
            state.LastAdvancedAt = now;
            while (state.DeathBlasts.TryPeek(out var blast) && blast.DueAt <= now)
            {
                state.DeathBlasts.Dequeue();
                due.Add(blast);
            }
            while (state.Reflections.TryDequeue(out var reflected)) reflections.Add(reflected);
            if (run.State == WonderlandRunState.Active)
            {
                var monsters = runtime.Map.SnapshotMonsters();
                var targets = _sessions.Values.Where(context => context.WorldReady &&
                    context.WorldInstanceId == runtime.InstanceId && context.Character.CurrentHp > 0 &&
                    run.Participants.Any(member => member.CharacterId == context.CharacterId) &&
                    IsCurrentAccountSession(context.AccountId, context.Session, context.Ownership)).ToArray();
                foreach (var monster in monsters)
                {
                    if (!runtime.Map.TryGetWonderlandSpawnPolicy(monster.ObjectId, out var policy) ||
                        !IsWonderlandPublishedStage(run, policy.Stage) || policy.IsAllied) continue;
                    if (!monster.IsAlive || !monster.IsSpawned)
                    {
                        state.Casters.Remove(monster.ObjectId);
                        continue;
                    }
                    var abilities = WonderlandBossAbilityPolicy.For(policy.MechanicKey);
                    if (abilities.Count == 0) continue;
                    if (!state.Casters.TryGetValue(monster.ObjectId, out var caster))
                        state.Casters.Add(monster.ObjectId, caster = new());
                    if (caster.Pending is { } interrupted &&
                        interrupted.Source.CastInterruptionRevision != monster.CastInterruptionRevision)
                        caster.Pending = null;
                    if ((monster.ControlsAt(now) & WonderlandSkillBlockingControls) != 0)
                    {
                        caster.Pending = null;
                        continue;
                    }
                    if (caster.Pending is { } pending)
                    {
                        if (pending.DueAt > now) continue;
                        caster.Pending = null;
                        due.Add(pending);
                    }
                    var maximumCastRange = Math.Min(policy.AggroRadius, abilities.Max(ability => ability.Radius));
                    var islandTargets = targets.Where(target => IsWonderlandCombatPosition(policy.Stage,
                        target.Character.PositionX, target.Character.PositionZ)).ToArray();
                    var target = islandTargets.Where(target => DistanceSquared(monster.X, monster.Z,
                            target.Character.PositionX, target.Character.PositionZ) <= maximumCastRange * maximumCastRange)
                        .OrderBy(target => DistanceSquared(monster.X, monster.Z,
                            target.Character.PositionX, target.Character.PositionZ)).FirstOrDefault();
                    if (target is null) continue;
                    var ability = abilities.FirstOrDefault(ability =>
                        (!caster.ReadyAt.TryGetValue(ability.Key, out var ready) || ready <= now) &&
                        DistanceSquared(monster.X, monster.Z, target.Character.PositionX, target.Character.PositionZ) <= ability.Radius * ability.Radius);
                    if (ability is null) continue;
                    caster.ReadyAt[ability.Key] = now + ability.Cooldown;
                    caster.Pending = new(monster, policy, ability, monster.X, monster.Z,
                        target.Character.PositionX, target.Character.PositionZ, target.CharacterId, now + ability.Windup);
                    foreach (var viewer in islandTargets)
                        AdmitWonderlandWarningLocked(runtime, state, viewer, monster, ability,
                            target.Character.PositionX, target.Character.PositionZ);
                }
                if (IsWonderlandPublishedStage(run, 6) && now >= state.NextFireAt)
                {
                    state.NextFireAt = now.AddSeconds(3);
                    ScheduleWonderlandGroundFireLocked(runtime, run, state, monsters,
                        targets.Where(target => WonderlandTerrainPolicy.GetIsland(6).Bounds.Contains(
                            target.Character.PositionX, target.Character.PositionZ)).ToArray(), now);
                }
                for (var index = state.GroundFires.Count - 1; index >= 0; index--)
                    if (state.GroundFires[index].DueAt <= now)
                    {
                        due.Add(state.GroundFires[index]);
                        state.GroundFires.RemoveAt(index);
                    }
                AdvanceWonderlandAlliedCombatLocked(runtime, run, state, monsters, now);
            }
            else
            {
                state.Casters.Clear();
                state.GroundFires.Clear();
            }
            while (state.AlliedHits.TryDequeue(out var alliedHit)) alliedHits.Add(alliedHit);
            while (state.Notifications.TryDequeue(out var notification)) notifications.Add(notification);
        }
        // Health delivery may wait for a viewer's visibility transition. Never
        // acquire that gate while holding the registry's authoritative gate.
        foreach (var alliedHit in alliedHits)
            await PublishWonderlandAlliedHitAsync(runtime, alliedHit, cancellationToken);
        foreach (var notification in notifications)
        {
            try { await notification; }
            catch (Exception error) { Console.WriteLine($"[wonderland] notice deferred: {error.GetType().Name}"); }
        }
        foreach (var reflection in reflections)
            await ExecuteWonderlandReflectionAsync(runtime, reflection, now);
        foreach (var cast in due)
            await ExecuteWonderlandCastAsync(runtime, cast, now);
    }

    private void AdmitWonderlandWarningLocked(WorldInstanceRuntime runtime, WonderlandCombatState state,
        GameSessionContext viewer, MonsterRuntimeSnapshot source, WonderlandBossAbility ability,
        float x, float z, bool announceBossCast = true)
    {
        if (viewer.WorldInstanceId != runtime.InstanceId ||
            !_playerLifeRevisions.TryGetValue(viewer.Session, out var life) ||
            !IsCurrentWonderlandPlayerLocked(viewer, life) ||
            !runtime.Map.TryGetWonderlandSpawnPolicy(source.ObjectId, out var policy) ||
            !IsWonderlandCombatPosition(policy.Stage, viewer.Character.PositionX, viewer.Character.PositionZ) ||
            !runtime.Map.TryGetWonderlandSnapshot(out var run) ||
            !run.Participants.Any(member => member.CharacterId == viewer.CharacterId)) return;
        var packets = new List<ReadOnlyMemory<byte>>
        {
            PacketBuilder.MonsterSkillCastVisual(source.ObjectId, 0, ability.NativeSkillId,
                source.X, source.Z, x, z)
        };
        // Announce each actual hostile boss windup, rather than a once-only
        // tutorial. Frequent support/terrain hazards keep their native visual
        // cue without filling the center announcement queue.
        if (announceBossCast && policy.IsBoss && !policy.IsAllied)
            packets.Add(PacketBuilder.CenteredRedAnnouncement(FormattableString.Invariant(
                $"{source.Definition.DisplayName} is casting {ability.Key} in {ability.Windup.TotalSeconds:0.0}s!")));
        // Stage 4: the windup warning (cast visual + optional banner) is a cue only; the hit itself is resolved
        // server-side. A viewer whose queue is full skips the cue instead of being disconnected mid-fight.
        if (viewer.Session.TryAdmitExactBatch(packets, out var write)) state.Notifications.Enqueue(write);
    }

    private void ScheduleWonderlandGroundFireLocked(WorldInstanceRuntime runtime, WonderlandSnapshot run,
        WonderlandCombatState state, IReadOnlyList<MonsterRuntimeSnapshot> monsters,
        IReadOnlyList<GameSessionContext> viewers, DateTimeOffset now)
    {
        var source = monsters.FirstOrDefault(monster => monster.IsAlive &&
            runtime.Map.TryGetWonderlandSpawnPolicy(monster.ObjectId, out var policy) && policy.MechanicKey == "platinum");
        if (source is null || !runtime.Map.TryGetWonderlandSpawnPolicy(source.ObjectId, out var spawn)) return;
        var geometry = WonderlandTerrainPolicy.GetIsland(6);
        var points = geometry.GroundFirePositions;
        if (points.Length < 3) return;
        // Keep the whole fire circle outside the relocated travel safe zones.
        var travelClearance = WonderlandTerrainPolicy.SafeZoneRadius + WonderlandBossAbilityPolicy.TerrainFire.Radius;
        var travelClearanceSquared = travelClearance * travelClearance;
        var offset = Random.Shared.Next(points.Length);
        var selected = new List<WonderlandPosition>(3);
        for (var index = 0; index < points.Length && selected.Count < 3; index++)
        {
            var point = points[(offset + index) % points.Length];
            if (!geometry.Bounds.Contains(point.X, point.Z) ||
                DistanceSquared(point.X, point.Z, geometry.Entrance.X, geometry.Entrance.Z) <= travelClearanceSquared ||
                DistanceSquared(point.X, point.Z, geometry.Exit.X, geometry.Exit.Z) <= travelClearanceSquared ||
                selected.Any(other => DistanceSquared(other.X, other.Z, point.X, point.Z) < 100)) continue;
            selected.Add(point);
            state.GroundFires.Add(new(source, spawn, WonderlandBossAbilityPolicy.TerrainFire,
                point.X, point.Z, point.X, point.Z, null, now.AddSeconds(1.5), GroundFire: true));
            foreach (var viewer in viewers)
                state.Notifications.Enqueue(PublishWonderlandGroundFireVisualAsync(runtime, viewer, source,
                    point.X, point.Z, impact: false, CancellationToken.None));
        }
    }

    private async Task ExecuteWonderlandCastAsync(WorldInstanceRuntime runtime, WonderlandScheduledCast cast,
        DateTimeOffset now)
    {
        (GameSessionContext Context, long Life)[] targets;
        var groundVisuals = new List<Task>();
        lock (_gate)
        {
            if (!TryGetWonderlandCombatLocked(runtime, now, out var run, out _, allowCompleted: true) ||
                !IsWonderlandPublishedStage(run, cast.Policy.Stage) || !runtime.Map.TryGetMonsterSnapshot(cast.Source.ObjectId, out var source) ||
                source.RuntimeInstanceId != cast.Source.RuntimeInstanceId || source.SpawnGeneration != cast.Source.SpawnGeneration ||
                !cast.DeathBlast && (!source.IsAlive || !source.IsSpawned) ||
                !cast.DeathBlast && !cast.GroundFire &&
                (source.CastInterruptionRevision != cast.Source.CastInterruptionRevision ||
                 (source.ControlsAt(now) & WonderlandSkillBlockingControls) != 0)) return;
            if (cast.GroundFire)
                foreach (var viewer in _sessions.Values.Where(viewer => viewer.WorldReady &&
                             viewer.WorldInstanceId == runtime.InstanceId &&
                             WonderlandTerrainPolicy.GetIsland(6).Bounds.Contains(
                                 viewer.Character.PositionX, viewer.Character.PositionZ)))
                    groundVisuals.Add(PublishWonderlandGroundFireVisualAsync(runtime, viewer, source,
                        cast.X, cast.Z, impact: true, CancellationToken.None));
            targets = _sessions.Values.Where(target => target.WorldReady && target.WorldInstanceId == runtime.InstanceId &&
                target.Character.CurrentHp > 0 && IsWonderlandCombatPosition(cast.Policy.Stage,
                    target.Character.PositionX, target.Character.PositionZ) && (cast.Ability.Shape != WonderlandAbilityShape.Single ||
                    target.CharacterId == cast.SelectedCharacterId) &&
                WonderlandBossAbilityPolicy.InShape(cast.Ability, cast.X, cast.Z, cast.AimX, cast.AimZ,
                    target.Character.PositionX, target.Character.PositionZ) &&
                _playerLifeRevisions.ContainsKey(target.Session))
                .Select(target => (target, _playerLifeRevisions[target.Session])).ToArray();
        }
        foreach (var visual in groundVisuals) await visual;
        foreach (var target in targets)
        for (var hit = 0; hit < cast.Ability.HitCount; hit++)
        {
            MonsterRuntimeUpdate attack;
            lock (_gate)
            {
                if (!IsCurrentWonderlandPlayerLocked(target.Context, target.Life)) continue;
                attack = BuildWonderlandAttack(target.Context, target.Life, cast.Source, cast.Ability, now,
                    cast.DeathBlast, cast.GroundFire);
            }
            await ProcessScheduledWonderlandAttackAsync(runtime, attack, now);
        }
    }

    private async Task ExecuteWonderlandReflectionAsync(WorldInstanceRuntime runtime,
        WonderlandReflectedHit reflected, DateTimeOffset now)
    {
        MonsterRuntimeUpdate attack;
        lock (_gate)
        {
            if (!IsCurrentWonderlandPlayerLocked(reflected.Target, reflected.LifeRevision)) return;
            var marker = WonderlandBossAbilityPolicy.ReflectionMarker;
            attack = BuildWonderlandAttack(reflected.Target, reflected.LifeRevision, reflected.Source,
                marker, now, deathBlast: false, groundFire: false) with { WonderlandReflectedDamage = reflected.Damage };
        }
        await ProcessScheduledWonderlandAttackAsync(runtime, attack, now);
    }

    private async Task ProcessScheduledWonderlandAttackAsync(WorldInstanceRuntime runtime,
        MonsterRuntimeUpdate attack, DateTimeOffset now)
    {
        try
        {
            await ProcessMonsterAttackAsync(runtime, attack, CancellationToken.None, now);
        }
        catch (MonsterAttackTargetUnavailableException error)
        {
            // Target membership can disappear after scheduling while the
            // status gate is awaited. Its exact authority fence prevents HP
            // mutation; tolerate the same expected race as ordinary attacks.
            // Do not hide unexpected simulation faults from supervision.
            Console.WriteLine($"[wonderland] skipped stale scheduled target character={error.TargetCharacterId} " +
                $"monster={attack.Monster.ObjectId} instance={runtime.InstanceId}");
        }
    }

    private MonsterRuntimeUpdate BuildWonderlandAttack(GameSessionContext target, long life,
        MonsterRuntimeSnapshot source, WonderlandBossAbility ability, DateTimeOffset now, bool deathBlast, bool groundFire) =>
        new(MonsterRuntimeUpdateKind.Attacked, source, TargetCharacterId: target.CharacterId,
            TargetX: target.Character.PositionX, TargetZ: target.Character.PositionZ, TargetObjectId: target.ObjectId,
            TargetLifeRevision: life, TargetVitalsRevision: target.Character.VitalsRevision,
            TargetOwnership: target.Ownership, TargetWorldInstanceId: target.WorldInstanceId,
            TargetWorldRevision: target.WorldRevision, TargetWorldMembershipEpoch: target.WorldMembershipEpoch,
            AttackEventId: AllocateRequiredMonsterAttackEventIdAbove(0), WonderlandAbility: ability,
            WonderlandDeathBlast: deathBlast, WonderlandGroundFire: groundFire);

    private static bool IsWonderlandCombatPosition(int island, float x, float z) =>
        WonderlandTerrainPolicy.IsCombatArea(island, x, z);

}
