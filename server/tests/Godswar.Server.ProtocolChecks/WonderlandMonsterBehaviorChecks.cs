using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Game;
using Godswar.Server.Game.WorldInstances;

namespace Godswar.Server.ProtocolChecks;

internal static partial class WonderlandMonsterBehaviorChecks
{
    public const string CheckName = "Wonderland stationary towers, authored cadence, safe zones, and Legacy/ECS profiles";

    public static Task RunAsync()
    {
        Check.True(MonsterBehaviorPolicy.Default.AttackInterval == MonsterMapRuntime.AttackCooldown &&
            !MonsterBehaviorPolicy.Default.Stationary && MonsterBehaviorPolicy.Default.MaximumRoamRadius == 8,
            "shared behavior defaults remain unchanged outside Wonderland");
        foreach (var mode in new[] { MonsterRuntimeMode.Legacy, MonsterRuntimeMode.Ecs })
        {
            CheckTower(mode, 1, WonderlandMonsterRole.ArrowTower, 25, TimeSpan.FromSeconds(1.92));
            CheckTower(mode, 7, WonderlandMonsterRole.LostTower, 30, TimeSpan.FromSeconds(1.5));
            CheckSafeZonesAndAllies(mode);
            CheckPassiveFiringHoops(mode);
            CheckRangedPetbirds(mode);
        }
        CheckProfiles();
        return Task.CompletedTask;
    }

    private static void CheckTower(MonsterRuntimeMode mode, int island, WonderlandMonsterRole role,
        float range, TimeSpan interval)
    {
        var map = WonderlandMapChecks.Create(mode);
        var now = WonderlandMapChecks.EnterIsland(map, island);
        map.TryGetWonderlandSnapshot(out var run);
        var tower = run.ActiveSpawns.First(p => p.Role == role);
        var runtime = map.InitializeMonsters([], now);
        var home = runtime.Snapshot().Single(m => m.ObjectId == tower.ObjectId);
        // Direct isolated runtime keeps this test independent of other bosses' attacks.
        var profiles = MonsterCombatProfileCatalog.Create(WonderlandMapChecks.Content());
        profiles = profiles.WithAuthoredOverrides(207, [(home.ObjectId, home.Definition.TemplateKey,
            WonderlandMonsterProfilePolicy.Resolve(profiles.Resolve(home.Definition), tower))]);
        var isolated = MonsterMapRuntimeFactory.Create(mode, 207, [home.Definition], now,
            respawnPolicy: MonsterRespawnPolicy.Never, monsterCombatProfiles: profiles,
            behaviorPolicy: new(range, range + 2, 0, stationary: true, attackInterval: interval));
        isolated.Advance(now.AddMinutes(1));
        var idle = isolated.Snapshot().Single();
        Check.True(idle.X == home.X && idle.Z == home.Z && !idle.IsMoving,
            $"{mode}: {role} never roams while idle");
        now = now.AddMinutes(1);
        var target = new MonsterCombatTarget(101, home.X + range - 1, home.Z, true);
        isolated.Advance(now, [target]);
        now += MonsterMapRuntime.TickInterval;
        Check.True(isolated.Advance(now, [target]).Updates.Any(u => u.Kind == MonsterRuntimeUpdateKind.Attacked),
            $"{mode}: stationary {role} attacks at its authored long reach");
        Check.True(!isolated.Advance(now + interval - TimeSpan.FromMilliseconds(1), [target]).Updates
            .Any(u => u.Kind == MonsterRuntimeUpdateKind.Attacked), "tower cannot attack before its authored cooldown");
        Check.True(isolated.Advance(now + interval, [target]).Updates.Any(u => u.Kind == MonsterRuntimeUpdateKind.Attacked),
            "tower attacks exactly when its authored cooldown expires");
        now += interval;
        target = target with { X = home.X + range + 1 };
        for (var i = 0; i < 100; i++)
        {
            var tick = isolated.Advance(now += MonsterMapRuntime.TickInterval, [target]);
            var snapshot = isolated.Snapshot().Single();
            Check.True(snapshot.X == home.X && snapshot.Z == home.Z && !snapshot.IsMoving &&
                tick.Updates.All(u => u.Kind != MonsterRuntimeUpdateKind.Attacked),
                "out-of-range targets cannot make a tower chase or deal a remote basic hit");
        }
    }

    private static void CheckSafeZonesAndAllies(MonsterRuntimeMode mode)
    {
        var map = WonderlandMapChecks.Create(mode);
        var now = WonderlandMapChecks.EnterIsland(map, 8);
        map.TryGetWonderlandSnapshot(out var run);
        var runtime = map.InitializeMonsters([], now);
        foreach (var point in new[] { run.Entrance, run.Exit })
        {
            var target = new MonsterCombatTarget(101, point.X, point.Z, true, WorldInstanceId: map.WorldInstanceId);
            for (var i = 0; i < 30; i++)
                Check.True(runtime.Advance(now += MonsterMapRuntime.TickInterval, [target]).Updates
                    .All(u => u.Kind != MonsterRuntimeUpdateKind.Attacked), "wide aggro respects entrance and exit safety");
        }
        var first = run.ActiveSpawns.First(p => p.Role == WonderlandMonsterRole.Minotaur);
        var active = new MonsterCombatTarget(101, first.Position.X, first.Position.Z - 15, true,
            WorldInstanceId: map.WorldInstanceId);
        runtime.Advance(now += MonsterMapRuntime.TickInterval, [active with { WorldInstanceId = default }]);
        Check.True(runtime.Snapshot().Where(m => m.IsSpawned && m.IsAlive).All(m =>
            m.CombatPhase == MonsterCombatPhase.None), "foreign exact-instance target cannot trigger wide aggro");
        runtime.Advance(now += MonsterMapRuntime.TickInterval, [active]);
        Check.True(runtime.Snapshot().Single(m => m.ObjectId == first.ObjectId).CombatPhase == MonsterCombatPhase.Chasing,
            "admitted target outside safe zones triggers the wide final group");

        var alliedMap = WonderlandMapChecks.Create(mode);
        var alliedAt = WonderlandMapChecks.EnterIsland(alliedMap, 5);
        alliedMap.TryGetWonderlandSnapshot(out var alliedRun);
        var ally = alliedRun.ActiveSpawns.First(p => p.IsAllied && p.IsBoss);
        var alliedRuntime = alliedMap.InitializeMonsters([], alliedAt);
        var nearby = new MonsterCombatTarget(101, ally.Position.X, ally.Position.Z, true,
            WorldInstanceId: alliedMap.WorldInstanceId);
        for (var i = 0; i < 100; i++) alliedRuntime.Advance(alliedAt += MonsterMapRuntime.TickInterval, [nearby]);
        Check.True(alliedRuntime.Snapshot().Where(m => alliedRun.ActiveSpawns.Any(p => p.ObjectId == m.ObjectId && p.IsAllied))
            .All(m => m.CombatPhase == MonsterCombatPhase.None), "allied marshal and expedition supports never acquire players");
    }

    private static void CheckProfiles()
    {
        var baseProfile = MonsterCombatProfileCatalog.Resolve(130, MonsterAttackDamageKind.Special, isBoss: true);
        var multi = WonderlandMonsterPlan.Create(7, 5, 0).Single(p => p.Role == WonderlandMonsterRole.MultiHead);
        var authored = WonderlandMonsterProfilePolicy.Resolve(baseProfile, multi);
        Check.True(authored.PhysicalAttack == 24000 && authored.ToTargetStats().Dodge == 18000 &&
            authored.PhysicalDefense == 8000 && authored.IsBoss, "boss attacker and target ratings share the authored profile");
        var rooster = WonderlandMonsterPlan.Create(3, 5, 0).Single(p => p.IsBoss);
        var magic = WonderlandMonsterProfilePolicy.Resolve(baseProfile, rooster);
        Check.True(magic.UsesMagicDamage && magic.MagicAttack == 18000 && magic.AuthoredAttackRange == 5,
            "magic bosses approach inside their area-skill radius with fixed magic attack");
        Check.True(baseProfile.AuthoredAttackRange is null, "ordinary profile retains its existing default reach selection");
    }
}
