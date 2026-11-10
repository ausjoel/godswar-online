namespace Godswar.Server.Game;

internal sealed partial class MonsterMapRuntime
{
    private static void SetNavigatedReturnStep(MonsterRuntimeState monster, DateTimeOffset now)
    {
        if (!monster.Navigation!.TryGetStep(monster.CurrentX, monster.CurrentZ,
                monster.HomeX, monster.HomeZ, MovementStep, out var dx, out var dz))
        {
            StopCombatMovement(monster);
            monster.NextMovementStepAt = now + ElementalMovementInterval(monster);
            return;
        }
        SetMovement(monster, now, 1, dx, dz,
            monster.CurrentX + dx, monster.CurrentZ + dz);
    }

    private static bool AdvanceNavigatedReturnHome(MonsterRuntimeState monster,
        DateTimeOffset now, List<MonsterRuntimeUpdate> updates)
    {
        var changed = false;
        while (monster.CombatPhase == MonsterCombatPhase.Returning && now >= monster.NextMovementStepAt)
        {
            var stepAt = monster.NextMovementStepAt;
            if (monster.IsMoving)
            {
                monster.CurrentX = monster.TargetX;
                monster.CurrentZ = monster.TargetZ;
                changed = true;
            }
            if (DistanceSquared(monster.CurrentX, monster.CurrentZ, monster.HomeX, monster.HomeZ) <= 0.00000001d)
            {
                CompleteReturnHome(monster, stepAt);
                updates.Add(new MonsterRuntimeUpdate(MonsterRuntimeUpdateKind.Returned,
                    CreateSnapshot(monster), MovementEndField: 1));
                if (ShouldRetireReturnedMonster(monster)) updates.Add(RetireReturnedMonster(monster, stepAt));
                break;
            }
            SetNavigatedReturnStep(monster, stepAt);
            updates.Add(new MonsterRuntimeUpdate(
                monster.IsMoving ? MonsterRuntimeUpdateKind.Started : MonsterRuntimeUpdateKind.Arrived,
                CreateSnapshot(monster), MovementMode: 1, MovementEndField: 1));
        }
        return changed;
    }
}
