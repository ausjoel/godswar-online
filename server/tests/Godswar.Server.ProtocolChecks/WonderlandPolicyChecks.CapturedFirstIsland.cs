using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Game;
using Godswar.Server.Game.WorldInstances;
using Godswar.Server.World.Systems.Combat;

namespace Godswar.Server.ProtocolChecks;

internal static partial class WonderlandPolicyChecks
{
    private static void CheckCapturedFirstIsland()
    {
        // Deduplicated external September11 first-island10020 capture:17 actors.
        var actors = WonderlandMonsterPlan.Create(1, 1, 0);
        (string Key, int Count, uint Hp, int Level)[] expected =
        [
            ("B_boss_xerxer_001", 1, 8_000_000, 200),
            ("B_normalC_AthensTower_001", 4, 2_000_000, 200),
            ("B_normale_robber_003", 8, 100_000, 120),
            ("B_normalg_flamingo_001", 2, 100_000, 120),
            ("B_normalg_famale_001", 2, 200_000, 120)
        ];
        Check.Equal(17, actors.Length, "first island contains exactly the seventeen observed external actors");
        foreach (var group in expected)
        {
            var matching = actors.Where(actor => actor.TemplateKey == group.Key).ToArray();
            Check.True(matching.Length == group.Count && matching.All(actor =>
                    actor.Stats.MaximumHealth == group.Hp && actor.Stats.Level == group.Level),
                $"captured first-island {group.Key} count, level and HP remain exact");
        }
        Check.True(actors[0].Position == new WonderlandPosition(171.267059f, 0, -151.701202f) &&
            actors.Where(actor => actor.Role == WonderlandMonsterRole.DemonicRaider)
                .Select(actor => actor.Position).SequenceEqual(new WonderlandPosition[]
                    { new(163.703934f, 0, -152.357788f), new(172.984177f, 0, -154.750122f) }),
            "Alpha and the two chickens occupy their captured first-visible positions");
        Check.True(actors.Where(actor => actor.Role == WonderlandMonsterRole.ArrowTower)
                .All(actor => actor.Stationary && actor.Stats.AttackRange == 25) &&
            actors.Where(actor => actor.Role is WonderlandMonsterRole.DemonicRaider or WonderlandMonsterRole.AlphaDemon)
                .All(actor => actor.Stats.AttackInterval == TimeSpan.FromSeconds(1.92)) &&
            WonderlandBossAbilityPolicy.For("alpha").Count == 0 && WonderlandBossAbilityPolicy.For("raider").Count == 0,
            "captured actors retain stationary towers and repeated native attacks without invented extra skill damage");

        // A calibration target, not a claim to recover the external server's formula.
        var target = new CombatTargetStats { Level = 143, PhysicalDefense = 5379, MagicDefense = 2920,
            PhysicalFlatAbsorption = 4791, MagicFlatAbsorption = 4791 };
        foreach (var (role, damage) in new[] { (WonderlandMonsterRole.AlphaDemon, 3953u),
                     (WonderlandMonsterRole.ArrowTower, 1u), (WonderlandMonsterRole.DemonicRaider, 24953u) })
        {
            var policy = actors.First(actor => actor.Role == role);
            var profile = WonderlandMonsterProfilePolicy.Resolve(
                MonsterCombatProfileCatalog.Resolve(checked((uint)policy.Stats.Level), MonsterAttackDamageKind.Physical, policy.IsBoss), policy);
            profile = WonderlandCapturedAttackPolicy.ApplyProfile(profile, policy, 0);
            var resolved = AuthoredCombatPveCurrent.ResolveBasicAttackForOutcome(profile.ToAttackerStats(), target,
                CombatHitOutcome.Normal);
            Check.Equal(damage, resolved.Damage,
                $"{role} inferred effective rating reproduces the latest captured normal hit against its reference mitigation");
        }
    }
}
