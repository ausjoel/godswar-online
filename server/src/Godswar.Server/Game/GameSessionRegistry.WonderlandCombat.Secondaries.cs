using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Game.WorldInstances;
using Godswar.Server.Packets;
using Godswar.Server.State;
using Godswar.Server.World.Systems.Combat;

namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
    // This terminal encounter effect intentionally bypasses all on-hit proc
    // planners. It cannot recursively trigger stat rebound or Gaia reflection.
    private void CommitWonderlandReflectionLocked(WorldInstanceRuntime runtime, WonderlandCombatState state,
        GameSessionContext target, long life, MonsterRuntimeSnapshot source, uint damage, DateTimeOffset now)
    {
        uint applied;
        bool killed;
        lock (target.Character.VitalsSync)
        {
            if (!IsCurrentWonderlandPlayerLocked(target, life)) return;
            var eventId = AllocateRequiredMonsterAttackEventIdAbove(0);
            if (_playerRuntimeMode == PlayerRuntimeMode.Ecs)
            {
                var decision = ResolvePlayerVitalsDamageEcs(target.Session, target.Character, target.ObjectId,
                    new(eventId, source.ObjectId, source.SpawnGeneration, target.CharacterId,
                        target.ObjectId, life, target.Character.VitalsRevision, damage, now),
                    beforeLethalCommit: () => state.Notifications.Enqueue(RequestSkillCastInterruptionAsync(
                        target.Session, SkillCastInterruptionReason.Death, CancellationToken.None)));
                if (!decision.Applied) return;
                applied = decision.AppliedDamage;
                killed = decision.Killed;
            }
            else
            {
                applied = Math.Min(damage, checked((uint)target.Character.CurrentHp));
                killed = applied == target.Character.CurrentHp;
                if (killed) state.Notifications.Enqueue(RequestSkillCastInterruptionAsync(
                    target.Session, SkillCastInterruptionReason.Death, CancellationToken.None));
                target.Character.CurrentHp -= checked((int)applied);
                target.Character.MarkVitalsChanged();
                if (killed)
                {
                    _playerLifeRevisions[target.Session] = checked(life + 1);
                    ApplyPlayerLifeAdvanceSideEffectsLocked(target.Session, now + PlayerRecoveryInterval,
                        GetOrCreatePlayerRecoveryDeadlineLocked(target.Session), now, resetIncomingDamage: true);
                }
            }
        }
        foreach (var viewer in _sessions.Values.Where(viewer => viewer.WorldReady &&
                     viewer.WorldInstanceId == runtime.InstanceId &&
                     IsCurrentAccountSession(viewer.AccountId, viewer.Session, viewer.Ownership)))
        {
            var local = ReferenceEquals(viewer.Session, target.Session);
            var objectId = local ? LocalPlayerObjectId : target.ObjectId;
            var packets = new List<ReadOnlyMemory<byte>>
            {
                PacketBuilder.PhysicalDamage(source.ObjectId, source.X, source.Y, source.Z,
                    objectId, applied, 0, (byte)CombatHitOutcome.Normal)
            };
            if (killed) packets.Add(PlayerDeathFrame(runtime, objectId,
                target.Character.PositionX, target.Character.PositionZ, target.MapId));
            // Stage 4 audit: 1-2 packets (damage, optional death frame), state-bearing for the target and watchers
            // (a dropped death frame would leave a dead player looking alive), always smaller than the 128-packet
            // queue. A rejection means the client is stuck, so the designed disconnect is kept.
            if (!viewer.Session.TryAdmitExactBatch(packets, out var sent)) viewer.Session.Disconnect();
            state.Notifications.Enqueue(sent);
        }
    }

    private void AdvanceWonderlandAlliedCombatLocked(WorldInstanceRuntime runtime, WonderlandSnapshot run,
        WonderlandCombatState state, IReadOnlyList<MonsterRuntimeSnapshot> monsters, DateTimeOffset now)
    {
        if (!IsWonderlandPublishedStage(run, 5)) return;
        foreach (var ally in monsters.Where(monster => monster.IsAlive && monster.IsSpawned))
        {
            if (!runtime.Map.TryGetWonderlandSpawnPolicy(ally.ObjectId, out var allyPolicy) ||
                !allyPolicy.IsAllied || allyPolicy.Stage != 5 ||
                (ally.ControlsAt(now) & HostileStatusControlFlags.NonAttackUsing) != 0) continue;
            if (!state.Casters.TryGetValue(ally.ObjectId, out var caster)) state.Casters.Add(ally.ObjectId, caster = new());
            if (caster.ReadyAt.TryGetValue("allied-aid", out var ready) && ready > now) continue;
            // Earlier allied attacks in this tick may already have changed a
            // target's health revision. Read current candidates for each ally.
            var enemy = runtime.Map.SnapshotMonsters().Where(monster => monster.IsAlive && monster.IsSpawned &&
                    runtime.Map.TryGetWonderlandSpawnPolicy(monster.ObjectId, out var enemyPolicy) &&
                    enemyPolicy.Stage == 5 && !enemyPolicy.IsAllied &&
                    DistanceSquared(ally.X, ally.Z, monster.X, monster.Z) <=
                        allyPolicy.Stats.AttackRange * allyPolicy.Stats.AttackRange)
                .OrderBy(monster => allyPolicy.IsBoss &&
                    runtime.Map.TryGetWonderlandSpawnPolicy(monster.ObjectId, out var candidatePolicy) &&
                    candidatePolicy.IsBoss ? 0 : 1)
                .ThenBy(monster => DistanceSquared(ally.X, ally.Z, monster.X, monster.Z)).FirstOrDefault();
            if (enemy is null || !runtime.Map.TryGetWonderlandSpawnPolicy(enemy.ObjectId, out var enemyPolicy)) continue;
            caster.ReadyAt["allied-aid"] = now + allyPolicy.Stats.AttackInterval;
            var allyProfile = WonderlandCapturedAttackPolicy.ApplyProfile(
                WonderlandMonsterProfilePolicy.Resolve(GameplayCatalogs.MonsterCombatProfiles.Resolve(ally.Definition), allyPolicy),
                allyPolicy, ally.ControlsAt(now));
            var resolution = AuthoredCombatPveCurrent.ResolveBasicAttack(
                allyProfile.ToAttackerStats(),
                new CombatTargetStats { Level = enemyPolicy.Stats.Level, PhysicalDefense = enemyPolicy.Stats.PhysicalDefense,
                    MagicDefense = enemyPolicy.Stats.MagicDefense, Dodge = enemyPolicy.Stats.Dodge,
                    CriticalResistance = enemyPolicy.IsBoss
                        ? GameplayCatalogs.MonsterCombatProfiles.Resolve(enemy.Definition).CriticalResistance : 0 },
                AllocateRequiredMonsterAttackEventIdAbove(0));
            if (!resolution.Hit || resolution.Damage == 0) continue;
            var damage = resolution.Damage;
            var result = InvokeWorldOwnerAuthoritativeMutation(runtime, map =>
            {
                MonsterDamageResult committed = null!;
                var applied = map.TryGetMonsterSnapshot(enemy.ObjectId, out var current) &&
                    current.RuntimeInstanceId == enemy.RuntimeInstanceId && current.SpawnGeneration == enemy.SpawnGeneration &&
                    current.HealthRevision == enemy.HealthRevision && map.TryApplyMonsterDamage(enemy.ObjectId,
                        damage, attackerCharacterId: null, enemy.SpawnGeneration, now, out committed);
                return (Applied: applied, Damage: committed);
            });
            if (!result.Applied) continue;
            state.AlliedHits.Enqueue(new(ally,
                WonderlandCapturedAttackPolicy.ResolvePrimarySkill(allyPolicy, ally, now), result.Damage,
                CaptureMonsterAttackPublicationRecipients(runtime, _sessions.Values.ToArray())));
            // No fictitious player attacker or reward callback is introduced.
            ObserveWonderlandMonsterDamageCommitted(runtime, result.Damage, now);
        }
    }
}
