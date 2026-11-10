using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed partial class MonsterMapRuntime
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
            if (!_monsters.TryGetValue(objectId, out var monster) ||
                monster.SpawnGeneration != expectedSpawnGeneration) return false;
            if (!monster.IsAlive || !monster.IsSpawned || monster.CombatPhase is
                MonsterCombatPhase.Returning or MonsterCombatPhase.AwaitingRetirement ||
                !monster.Controls.TryApply(definition, now, out var next, out var expiresAt))
            { result = new(false, CreateSnapshot(monster), null); return true; }
            var stop = definition.Control.HasFlag(HostileStatusControlFlags.NonMoving);
            var wasMoving = stop && monster.IsMoving;
            monster.Controls = next;
            monster.AggroCharacterId = MonsterAggroPolicy.SelectLeader(monster.DamageThreat,
                monster.AggroCharacterId) ?? attackerCharacterId;
            if (stop)
            {
                StopCombatMovement(monster);
                monster.CombatPhase = MonsterCombatPhase.None;
                monster.HasSentInitialChase = false;
                monster.NextMovementAt = expiresAt;
                monster.NextMovementStepAt = expiresAt + ElementalMovementInterval(monster);
            }
            if (definition.Control.HasFlag(HostileStatusControlFlags.NonAttackUsing))
            {
                monster.CombatPhase = MonsterCombatPhase.None;
                monster.NextAttackAt = expiresAt + TickInterval;
            }
            if (definition.StatusId == MonsterStunSkillCatalog.StunnedStatusId) monster.StunnedUntil = expiresAt;
            var snapshot = CreateSnapshot(monster);
            if (wasMoving) _pendingUpdates.Enqueue(new(MonsterRuntimeUpdateKind.Arrived, snapshot, MovementEndField: 1));
            result = new(true, snapshot, expiresAt);
            return true;
        }
    }
}
