using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Game;

namespace Godswar.Server.ProtocolChecks;

internal static class WonderlandNavigationChecks
{
    public const string CheckName = "Wonderland final island collision navigation";

    public static Task RunAsync()
    {
        var grid = MonsterNavigationGrid.WonderlandFinalIsland;
        var plan = WonderlandMonsterPlan.Create(8, 1, 0);
        Check.True(plan.All(p => p.AggroRadius == 24 && p.LeashRadius == 64),
            "all final chamber actors use local24-unit acquisition and64-unit leash");
        Check.True(plan.All(p => grid.IsWalkable(p.Position.X, p.Position.Z)) &&
            grid.IsWalkable(56, -112) && !grid.IsWalkable(40, -42) && !grid.IsWalkable(0, -130),
            "native collision resource contains exact captured spawns, corridor, wall and out-of-island void");
        Check.True(!grid.CanTraverse(61.0597801f, -40.3367691f, 35.5f, -49.5f),
            "straight chase toward the lower corridor would cut through its native wall");
        Check.True(grid.CanTraverse(56, -112, 55.5f, -111.5f) &&
            grid.CanTraverse(55.5f, -111.5f, 56, -112),
            "a diagonal ending exactly on a native cell boundary stays symmetric in both directions");
        foreach (var point in plan.Where(p => p.IsBoss || p.Role == WonderlandMonsterRole.ChestGuard))
        {
            CheckRoutedWalk(grid, 56, -112, point.Position.X, point.Position.Z);
            CheckRoutedWalk(grid, point.Position.X, point.Position.Z, 56, -112);
        }
        var invalid = new MonsterNavigation(grid);
        Check.True(!invalid.TryGetStep(61, -40, 40, -42, .38f, out _, out _),
            "an unreachable blocked destination cannot create a straight fallback through scenery");
        foreach (var mode in new[] { MonsterRuntimeMode.Legacy, MonsterRuntimeMode.Ecs })
            CheckActualChaseAndReturn(mode, grid);
        return Task.CompletedTask;
    }

    private static void CheckRoutedWalk(MonsterNavigationGrid grid, float x, float z, float goalX, float goalZ)
    {
        var navigation = new MonsterNavigation(grid);
        for (var step = 0; step < 6000; step++)
        {
            if (DistanceSquared(x, z, goalX, goalZ) < .000001f) return;
            Check.True(navigation.TryGetStep(x, z, goalX, goalZ, .38f, out var dx, out var dz),
                $"native corridor supplies a route to ({goalX},{goalZ}) at ({x},{z})");
            Check.True(dx * dx + dz * dz <= .38f * .38f + .00001f &&
                grid.CanTraverse(x, z, x + dx, z + dz), "each route segment respects speed and native collision");
            x += dx; z += dz;
        }
        throw new InvalidOperationException("Native final-island route did not reach its destination.");
    }

    private static void CheckActualChaseAndReturn(MonsterRuntimeMode mode, MonsterNavigationGrid grid)
    {
        var map = WonderlandMapChecks.Create(mode);
        var now = WonderlandMapChecks.EnterIsland(map, 8);
        var runtime = map.InitializeMonsters([], now);
        var original = runtime.Snapshot().Single(m => m.ObjectId == 46800);
        var far = new MonsterCombatTarget(101, 61, -83, true, WorldInstanceId: map.WorldInstanceId);
        runtime.Advance(now += MonsterMapRuntime.TickInterval, [far]);
        Check.True(runtime.Snapshot().Single(m => m.ObjectId == original.ObjectId).CombatPhase == MonsterCombatPhase.None,
            $"{mode}: Minotaur no longer pulls from43 units down the entry corridor");
        Check.True(runtime.TryApplyDamage(original.ObjectId, 1, 101, original.SpawnGeneration, now, out _),
            "direct hit acquires a reachable player beyond ordinary proximity detection");
        var target = new MonsterCombatTarget(101, 35.5f, -49.5f, true, WorldInstanceId: map.WorldInstanceId);
        var previous = original;
        var attacked = false;
        for (var step = 0; step < 500 && !attacked; step++)
        {
            var tick = runtime.Advance(now += MonsterMapRuntime.TickInterval, [target]);
            var current = runtime.Snapshot().Single(m => m.ObjectId == original.ObjectId);
            ValidateTick(mode, grid, previous, current, tick);
            foreach (var update in tick.Updates.Where(u => u.Monster.ObjectId == original.ObjectId &&
                u.Kind == MonsterRuntimeUpdateKind.Attacked))
            {
                Check.True(grid.CanTraverse(update.Monster.X, update.Monster.Z, target.X, target.Z),
                    "boss may only attack with a clear native segment to the target");
                attacked = true;
            }
            previous = current;
        }
        Check.True(attacked && previous.X < 46, $"{mode}: boss routes around the corner and reaches the lower corridor");
        runtime.ClearAggroForCharacter(101, now);
        var returned = false;
        for (var step = 0; step < 500 && !returned; step++)
        {
            var tick = runtime.Advance(now += MonsterMapRuntime.TickInterval);
            var current = runtime.Snapshot().Single(m => m.ObjectId == original.ObjectId);
            ValidateTick(mode, grid, previous, current, tick);
            returned = tick.Updates.Any(u => u.Monster.ObjectId == original.ObjectId &&
                u.Kind == MonsterRuntimeUpdateKind.Returned);
            previous = current;
        }
        Check.True(returned && previous.X == original.HomeX && previous.Z == original.HomeZ &&
            previous.SpawnGeneration == original.SpawnGeneration,
            $"{mode}: reset follows the corridor back to exact home without a wall shortcut or replacement spawn");
        for (var step = 0; step < 1200; step++)
        {
            var tick = runtime.Advance(now += MonsterMapRuntime.TickInterval);
            var current = runtime.Snapshot().Single(m => m.ObjectId == original.ObjectId);
            ValidateTick(mode, grid, previous, current, tick);
            previous = current;
        }
    }

    private static void ValidateTick(MonsterRuntimeMode mode, MonsterNavigationGrid grid,
        MonsterRuntimeSnapshot before, MonsterRuntimeSnapshot after, MonsterRuntimeTick tick)
    {
        Check.True(grid.CanTraverse(before.X, before.Z, after.X, after.Z),
            $"{mode}: actual movement never traverses a blocked cell");
        foreach (var update in tick.Updates.Where(u => u.Monster.ObjectId == before.ObjectId &&
            u.Kind == MonsterRuntimeUpdateKind.Started))
        {
            var m = update.Monster;
            Check.True(grid.CanTraverse(m.X, m.Z, m.X + m.VelocityX * m.RemainingMovementTicks,
                m.Z + m.VelocityZ * m.RemainingMovementTicks),
                $"{mode}: the full advertised chase, patrol or return segment stays on native terrain");
        }
    }

    private static float DistanceSquared(float x, float z, float targetX, float targetZ) =>
        (targetX - x) * (targetX - x) + (targetZ - z) * (targetZ - z);
}
