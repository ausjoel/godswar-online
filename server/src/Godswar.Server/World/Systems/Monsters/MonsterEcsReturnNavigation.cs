using Godswar.Server.Ecs;
using Godswar.Server.Game;
using Godswar.Server.World.Components.Monsters;

namespace Godswar.Server.World.Systems.Monsters;

internal static class MonsterEcsReturnNavigation
{
    public static void SetStep(EcsWorld world, EntityId entity, DateTimeOffset now)
    {
        ref var transform = ref world.Get<MonsterTransformComponent>(entity);
        ref var movement = ref world.Get<MonsterMovementComponent>(entity);
        if (!movement.Navigation!.TryGetStep(transform.X, transform.Z, transform.HomeX, transform.HomeZ,
                MonsterEcsRules.MovementStep, out var dx, out var dz))
        {
            MonsterEcsState.StopCombatMovement(ref movement);
            movement.NextMovementStepAt = now + MonsterEcsState.ElementalMovementInterval(in movement);
            return;
        }
        MonsterEcsState.SetMovement(ref transform, ref movement, now, 1, dx, dz,
            transform.X + dx, transform.Z + dz);
    }

    public static bool Advance(EcsWorld world, EntityId entity, DateTimeOffset now, EcsEventBuffer events)
    {
        ref var transform = ref world.Get<MonsterTransformComponent>(entity);
        ref var movement = ref world.Get<MonsterMovementComponent>(entity);
        ref var combat = ref world.Get<MonsterCombatComponent>(entity);
        var changed = false;
        while (combat.Phase == MonsterCombatPhase.Returning && now >= movement.NextMovementStepAt)
        {
            var stepAt = movement.NextMovementStepAt;
            if (movement.IsMoving)
            {
                transform.X = movement.TargetX;
                transform.Z = movement.TargetZ;
                changed = true;
            }
            if (MonsterEcsState.DistanceSquared(transform.X, transform.Z,
                    transform.HomeX, transform.HomeZ) <= 0.00000001d)
            {
                MonsterEcsState.CompleteReturnHome(world, entity, stepAt);
                events.Publish(new MonsterEcsUpdateEvent(new MonsterRuntimeUpdate(MonsterRuntimeUpdateKind.Returned,
                    MonsterEcsState.Snapshot(world, entity), MovementEndField: 1)));
                if (MonsterEcsState.ShouldRetireReturnedMonster(world, entity))
                    events.Publish(new MonsterEcsUpdateEvent(MonsterEcsState.RetireReturnedMonster(world, entity, stepAt)));
                break;
            }
            SetStep(world, entity, stepAt);
            events.Publish(new MonsterEcsUpdateEvent(new MonsterRuntimeUpdate(
                movement.IsMoving ? MonsterRuntimeUpdateKind.Started : MonsterRuntimeUpdateKind.Arrived,
                MonsterEcsState.Snapshot(world, entity), MovementMode: 1, MovementEndField: 1)));
        }
        return changed;
    }
}
