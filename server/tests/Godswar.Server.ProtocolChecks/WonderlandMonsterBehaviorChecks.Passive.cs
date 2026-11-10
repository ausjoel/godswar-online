using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Game;
using Godswar.Server.Game.WorldInstances;

namespace Godswar.Server.ProtocolChecks;

internal static partial class WonderlandMonsterBehaviorChecks
{
    private static void CheckPassiveFiringHoops(MonsterRuntimeMode mode)
    {
        Check.Throws<ArgumentOutOfRangeException>(() => new MonsterBehaviorPolicy(0, 2, 0, stationary: true),
            "zero aggro is rejected unless nonattacking passive behavior is explicit");
        Check.Throws<ArgumentException>(() => new MonsterBehaviorPolicy(0, 2, 0, passive: true),
            "a moving passive actor cannot bypass ordinary movement bounds");
        Check.Throws<ArgumentException>(() => new MonsterBehaviorPolicy(1, 2, 0, stationary: true, passive: true),
            "passive actors cannot declare an acquisition radius");
        Check.Throws<ArgumentException>(() => new MonsterBehaviorPolicy(0, 3, 1, stationary: true, passive: true),
            "passive actors cannot declare a roam radius");

        var map = WonderlandMapChecks.Create(mode);
        var now = WonderlandMapChecks.EnterIsland(map, 6);
        map.TryGetWonderlandSnapshot(out var run);
        var hoops = run.ActiveSpawns.Where(p => p.Role == WonderlandMonsterRole.FiringHoop).ToArray();
        Check.Equal(3, hoops.Length, "all three captured Firing Hoops publish without inventing aggro");
        foreach (var policy in hoops)
        {
            Check.True(map.TryGetMonsterSnapshot(policy.ObjectId, out var home), "captured passive actor is published");
            var profiles = MonsterCombatProfileCatalog.Create(WonderlandMapChecks.Content());
            var profile = WonderlandMonsterProfilePolicy.Resolve(profiles.Resolve(home.Definition), policy);
            Check.Throws<ArgumentOutOfRangeException>(() => MonsterAttackRangePolicy.Resolve(profile, home.Definition),
                "ordinary combat profiles still reject zero attack reach");
            Check.Equal(0f, MonsterAttackRangePolicy.Resolve(profile, home.Definition, passive: true),
                "only explicitly passive hydration accepts the captured zero reach");
            profiles = profiles.WithAuthoredOverrides(207, [(home.ObjectId, home.Definition.TemplateKey, profile)]);
            var isolated = MonsterMapRuntimeFactory.Create(mode, 207, [home.Definition], now,
                respawnPolicy: MonsterRespawnPolicy.Never, monsterCombatProfiles: profiles,
                behaviorPolicy: new(0, 2, 0, stationary: true, passive: true));
            var target = new MonsterCombatTarget(101, home.X, home.Z, true);
            // Exact coincidence would pass a naive zero-radius distance test.
            // Damage threat must not turn this scenery actor into a retaliator.
            Check.True(isolated.TryApplyDamage(home.ObjectId, 100, 101, home.SpawnGeneration, now, out var hit) &&
                hit.AfterHealth == home.MaximumHealth - 100, "passive actors retain ordinary damage authority");
            for (var tick = 0; tick < 100; tick++)
            {
                var update = isolated.Advance(now += MonsterMapRuntime.TickInterval, [target]);
                var actor = isolated.Snapshot().Single();
                Check.True(actor.X == home.X && actor.Z == home.Z && !actor.IsMoving &&
                    actor.CombatPhase == MonsterCombatPhase.None &&
                    update.Updates.All(u => u.Kind is not (MonsterRuntimeUpdateKind.Attacked or
                        MonsterRuntimeUpdateKind.Started)),
                    $"{mode}: passive Firing Hoop never acquires, attacks or moves even after a direct hit");
            }
            var remaining = isolated.Snapshot().Single().CurrentHealth;
            Check.True(isolated.TryApplyDamage(home.ObjectId, remaining, 101, home.SpawnGeneration, now, out var lethal) &&
                lethal.Killed, "passive behavior preserves a legitimate death transition");
            isolated.Advance(now);
            isolated.Advance(now + MonsterMapRuntime.DefaultCorpseDespawnDelay);
            var retired = isolated.Snapshot().Single();
            Check.True(!retired.IsAlive && !retired.IsSpawned && retired.RespawnAt is null,
                "passive behavior still processes finite corpse retirement without respawn");
        }
    }
}
