using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Game;
using Godswar.Server.Game.WorldInstances;

namespace Godswar.Server.ProtocolChecks;

internal static partial class WonderlandMonsterBehaviorChecks
{
    private static void CheckRangedPetbirds(MonsterRuntimeMode mode)
    {
        CheckRangedBuffBirds(mode, 3, WonderlandMonsterRole.Petbird, 24, 5_000_000);
        CheckRangedBuffBirds(mode, 7, WonderlandMonsterRole.PutridBird, 5, 500_000);
    }

    private static void CheckRangedBuffBirds(MonsterRuntimeMode mode, int island,
        WonderlandMonsterRole role, int count, uint health)
    {
        var map = WonderlandMapChecks.Create(mode);
        var now = WonderlandMapChecks.EnterIsland(map, island);
        map.TryGetWonderlandSnapshot(out var run);
        var birds = run.ActiveSpawns.Where(p => p.Role == role).ToArray();
        Check.True(birds.Length == count && birds.All(p => p.Stats.AttackRange == 12 &&
            p.AggroRadius == 12 && !p.Stationary && p.Stats.MaximumHealth == health &&
            p.Stats.PhysicalAttack == 4000 && p.Stats.MagicAttack == 4000),
            $"all {count} {role} keep their stats and mobility with moderate attack and detection range");
        foreach (var bird in birds.DistinctBy(p => p.TemplateKey))
        {
            var home = map.SnapshotMonsters().Single(m => m.ObjectId == bird.ObjectId);
            var profiles = MonsterCombatProfileCatalog.Create(WonderlandMapChecks.Content());
            profiles = profiles.WithAuthoredOverrides(207, [(home.ObjectId, home.Definition.TemplateKey,
                WonderlandMonsterProfilePolicy.Resolve(profiles.Resolve(home.Definition), bird))]);
            IMonsterMapRuntime CreateRuntime() => MonsterMapRuntimeFactory.Create(mode, 207, [home.Definition], now,
                respawnPolicy: MonsterRespawnPolicy.Never, monsterCombatProfiles: profiles,
                behaviorPolicy: new(bird.AggroRadius, bird.LeashRadius, 2, stationary: bird.Stationary,
                    attackInterval: bird.Stats.AttackInterval));
            var distantRuntime = CreateRuntime();
            var distant = new MonsterCombatTarget(101, home.X + 25, home.Z, true);
            distantRuntime.Advance(now, [distant]);
            Check.True(distantRuntime.Advance(now + MonsterMapRuntime.TickInterval, [distant]).Updates
                .All(u => u.Kind != MonsterRuntimeUpdateKind.Attacked) &&
                distantRuntime.Snapshot().Single().CombatPhase == MonsterCombatPhase.None,
                $"{role} no longer acquires a player at the former 25-unit range");
            var runtime = CreateRuntime();
            var target = new MonsterCombatTarget(101, home.X + 12, home.Z, true);
            var at = now;
            runtime.Advance(at, [target]);
            at += MonsterMapRuntime.TickInterval;
            Check.True(runtime.Advance(at, [target]).Updates.Any(u => u.Kind == MonsterRuntimeUpdateKind.Attacked) &&
                runtime.Snapshot().Single() is { IsMoving: false } ready && ready.X == home.X && ready.Z == home.Z,
                $"{mode}: {bird.TemplateKey} shoots at12 units without approaching into melee");
            Check.True(!runtime.Advance(at + bird.Stats.AttackInterval - TimeSpan.FromMilliseconds(1), [target])
                .Updates.Any(u => u.Kind == MonsterRuntimeUpdateKind.Attacked),
                "ranged Petbird cannot bypass its original1.92-second cooldown");
            at += bird.Stats.AttackInterval;
            Check.True(runtime.Advance(at, [target]).Updates.Any(u => u.Kind == MonsterRuntimeUpdateKind.Attacked),
                "ranged Petbird attacks when its original cooldown expires");
            target = target with { X = home.X + 17 };
            var chase = runtime.Advance(at += MonsterMapRuntime.TickInterval, [target]);
            Check.True(chase.Updates.All(u => u.Kind != MonsterRuntimeUpdateKind.Attacked) &&
                runtime.Snapshot().Single() is { IsMoving: true, CombatPhase: MonsterCombatPhase.Chasing },
                "buff bird chases an acquired target that leaves its12-unit reach");
            runtime.Advance(at + TimeSpan.FromSeconds(3), [target]);
            var stopped = runtime.Snapshot().Single();
            Check.True(!stopped.IsMoving && stopped.X > home.X && Math.Abs(target.X - stopped.X - 12) < 0.01,
                "chasing buff bird stops at ranged reach rather than running up to the player");
        }
    }
}
