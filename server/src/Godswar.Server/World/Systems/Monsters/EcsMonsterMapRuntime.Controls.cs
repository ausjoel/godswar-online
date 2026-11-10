using Godswar.Server.Game;
using Godswar.Server.State;
using Godswar.Server.World.Components.Monsters;

namespace Godswar.Server.World.Systems.Monsters;

internal sealed partial class EcsMonsterMapRuntime
{
    public bool TryApplyControl(uint objectId, int attackerCharacterId,
        HostileStatusEffectDefinition definition, uint expectedSpawnGeneration,
        DateTimeOffset now, out MonsterControlResult result)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(attackerCharacterId);
        definition.Validate();
        lock (_gate)
        {
            result = null!;
            if (!_entities.TryGetValue(objectId, out var entity)) return false;
            ref var vitals = ref _world.Get<MonsterVitalsComponent>(entity);
            if (vitals.SpawnGeneration != expectedSpawnGeneration) return false;
            ref var combat = ref _world.Get<MonsterCombatComponent>(entity);
            ref var movement = ref _world.Get<MonsterMovementComponent>(entity);
            if (!vitals.IsAlive || !vitals.IsSpawned || combat.Phase is
                MonsterCombatPhase.Returning or MonsterCombatPhase.AwaitingRetirement ||
                !(combat.Controls ?? MonsterControlState.Empty).TryApply(definition, now, out var next, out var expiresAt))
            { result = new(false, MonsterEcsState.Snapshot(_world, entity), null); return true; }
            var stop = definition.Control.HasFlag(HostileStatusControlFlags.NonMoving);
            var wasMoving = stop && movement.IsMoving;
            combat.Controls = next;
            combat.AggroCharacterId = MonsterAggroPolicy.SelectLeader(combat.DamageThreat,
                combat.AggroCharacterId) ?? attackerCharacterId;
            if (stop)
            {
                MonsterEcsState.StopCombatMovement(ref movement);
                combat.Phase = MonsterCombatPhase.None;
                combat.HasSentInitialChase = false;
                movement.NextMovementAt = expiresAt;
                movement.NextMovementStepAt = expiresAt + MonsterEcsState.ElementalMovementInterval(in movement);
            }
            if (definition.Control.HasFlag(HostileStatusControlFlags.NonAttackUsing))
            {
                combat.Phase = MonsterCombatPhase.None;
                combat.NextAttackAt = expiresAt + MonsterEcsRules.TickInterval;
            }
            if (definition.StatusId == MonsterStunSkillCatalog.StunnedStatusId) combat.StunnedUntil = expiresAt;
            var snapshot = MonsterEcsState.Snapshot(_world, entity);
            if (wasMoving) _pendingUpdates.Enqueue(new(MonsterRuntimeUpdateKind.Arrived, snapshot, MovementEndField: 1));
            result = new(true, snapshot, expiresAt);
            return true;
        }
    }
}
