using System.Buffers.Binary;
using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Game;
using Godswar.Server.Game.WorldInstances;
using Godswar.Server.Packets;
using Godswar.Server.State;
using Godswar.Server.World.Systems.Combat;

namespace Godswar.Server.ProtocolChecks;

internal static partial class WonderlandCombatChecks
{
    private static void CheckCapturedAttackEvidence()
    {
        // Literal initialized10046 headers from SHA81EA1117...666DF44.
        // Earlier same-file appearances fill the opposite faction and Assaulter.
        (string Template, string Header)[] native =
        [
            ("B_boss_xerxer_001", "18003E27FA57000064000000F50A0000"),
            ("B_bosse_bull_001", "18003E27DA51000064000000F30A0000"),
            ("B_bosse_dracoladon_003", "18003E27E851000064000000E5070000"),
            ("B_bosse_dragon_014", "18003E270452000064000000E6070000"),
            ("B_bosse_dryad_001", "18003E27E053000064000000E6070000"),
            ("B_bosse_flamingo_001", "18003E270858000064000000D0070000"),
            ("B_bosse_gadsguard_007", "18003E279654000064000000F30A0000"),
            ("B_bosse_greecewarrior_001", "18003E2720520000CC000000D4070000"),
            ("B_bosse_greecewarrior_002", "18003E272E52000064000000D4070000"),
            ("B_bosse_kingofscorpion_01", "18003E27A45400006400000090080000"),
            ("B_bosse_male_001", "18003E27B25400006400000082080000"),
            ("B_bosse_pan_002", "18003E27F65100006400000082080000"),
            ("B_bossf_dracoladon_003", "18003E27D253000064000000E5070000"),
            ("B_normalC_AthensTower_001", "18003E278651000064000000DF070000"),
            ("B_normalD_wraith_002", "18003E2716580000CC000000D0070000"),
            ("B_normale_cyclops_004", "18003E27A853000064000000D0070000"),
            ("B_normale_fairy_004", "18003E271252000064000000DB070000"),
            ("B_normale_female_001", "18003E276C540000640000001C080000"),
            ("B_normale_mage006", "18003E275852000064000000A3080000"),
            ("B_normale_mage007", "18003E2782520000CC000000A3080000"),
            ("B_normale_mage014", "18003E27AC52000064000000A3080000"),
            ("B_normale_mage017", "18003E27BA520000CC000000A3080000"),
            ("B_normale_robber_003", "18003E27C054000064000000D0070000"),
            ("B_normale_sprider_007", "18003E273E55000064000000D0070000"),
            ("B_normale_stymphalianbird_002", "18003E270256000064000000DE0A0000"),
            ("B_normale_stymphalianbird_003", "18003E270053000064000000D0070000"),
            ("B_normale_stymphalianbird_005", "18003E27AA56000064000000DE0A0000"),
            ("B_normale_wraith_001", "18003E277053000064000000D0070000"),
            ("B_normalf_wraith_001", "18003E27525700006400000083080000"),
            ("B_normalf_wraith_002", "18003E278A57000064000000D0070000"),
            ("B_normalg_famale_001", "18003E27ED530000F0000000D0070000"),
            ("B_normalg_flamingo_001", "18003E27885400006400000083080000"),
            ("B_normalg_wraith_003", "18003E27B457000064000000D0070000"),
            ("B_normalg_wraith_005", "18003E27EC57000064000000D0070000"),
        ];
        var plan = Enumerable.Range(1, 8).SelectMany(i => WonderlandMonsterPlan.Create(i, 1, 0)).ToArray();
        Check.True(plan.Where(p => p.Role is not (WonderlandMonsterRole.FlameRooster or
                WonderlandMonsterRole.Atlas or WonderlandMonsterRole.LostTower)).All(p =>
                p.Stats.AttackInterval == TimeSpan.FromSeconds(1.92)) &&
            plan.Single(p => p.Role == WonderlandMonsterRole.FlameRooster).Stats.AttackInterval == TimeSpan.FromSeconds(2) &&
            plan.Where(p => p.Role == WonderlandMonsterRole.Atlas).All(p => p.Stats.AttackInterval == TimeSpan.FromSeconds(3.84)),
            "observed ordinary cadence and double-tick Atlas attacks retain the explicit Rooster exception");
        foreach (var (template, hex) in native)
        {
            var packet = Convert.FromHexString(hex);
            var skill = BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(12));
            var spawn = plan.First(p => p.TemplateKey == template);
            var configuredSkill = spawn.Role is WonderlandMonsterRole.Petbird or WonderlandMonsterRole.PutridBird
                ? 2015u : skill;
            Check.True(WonderlandCapturedAttackPolicy.PrimarySkillId(template) == skill &&
                WonderlandCapturedAttackPolicy.ResolvePrimarySkill(spawn, HostileStatusControlFlags.None) == configuredSkill &&
                PacketBuilder.SkillCastImpact(BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(4)),
                    BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(8)), skill, 0, 0)
                    .AsSpan(0, 16).SequenceEqual(packet),
                $"{template} retains its native evidence and the approved Petbird Fireball override");
        }
        var target = new CombatTargetStats { Level = 143, PhysicalDefense = 5379, MagicDefense = 2920,
            PhysicalFlatAbsorption = 4791, MagicFlatAbsorption = 4791 };
        // Literal normal10026 values grouped with the two preceding10046s.
        (WonderlandMonsterRole Role, uint Damage, uint? Silenced, bool Magic)[] hits =
        [
            (WonderlandMonsterRole.AlphaDemon, 3953, null, false),
            (WonderlandMonsterRole.Derskey, 6753, null, true),
            (WonderlandMonsterRole.Monkeyface, 6753, null, false),
            (WonderlandMonsterRole.RockSpirit, 14453, null, true),
            (WonderlandMonsterRole.SpartanMarshal, 15993, 4813, false),
            (WonderlandMonsterRole.PlatinumDragon, 15153, 4213, true),
            (WonderlandMonsterRole.MultiHead, 15153, 4213, true),
            (WonderlandMonsterRole.Minotaur, 12353, 2213, false),
            (WonderlandMonsterRole.Deer, 15153, 4213, true),
            (WonderlandMonsterRole.DragonKing, 15853, 4713, true),
            (WonderlandMonsterRole.ScorpionKing, 22153, 9213, false),
            (WonderlandMonsterRole.DemonicRaider, 24953, null, true),
            (WonderlandMonsterRole.Atlas, 25653, null, true)
        ];
        var silence = HostileStatusControlFlags.NonMagicUsing | HostileStatusControlFlags.NonTechniqueUsing;
        foreach (var (role, expected, silenced, magic) in hits)
        {
            var spawn = plan.First(p => p.Role == role);
            var baseline = WonderlandMonsterProfilePolicy.Resolve(MonsterCombatProfileCatalog.Resolve(
                (uint)spawn.Stats.Level, MonsterAttackDamageKind.Physical), spawn);
            var calibrated = WonderlandCapturedAttackPolicy.ApplyProfile(baseline, spawn, HostileStatusControlFlags.None);
            var resolution = AuthoredCombatPveCurrent.ResolveBasicAttackForOutcome(calibrated.ToAttackerStats(),
                target, CombatHitOutcome.Normal);
            Check.True(resolution.Damage == expected && resolution.Channel ==
                (magic ? CombatDamageChannel.Magic : CombatDamageChannel.Physical),
                $"{role} inferred effective rating reproduces its captured normal hit and native channel");
            if (silenced is not { } fallback) continue;
            var physical = WonderlandCapturedAttackPolicy.ApplyProfile(baseline, spawn, silence);
            resolution = AuthoredCombatPveCurrent.ResolveBasicAttackForOutcome(physical.ToAttackerStats(),
                target, CombatHitOutcome.Normal);
            Check.True(resolution.Channel == CombatDamageChannel.Physical && resolution.Damage == fallback &&
                WonderlandCapturedAttackPolicy.ResolvePrimarySkill(spawn, silence) == 2000,
                $"{role} retains its independently captured physical damage and2000/2000 silence fallback");
        }
        foreach (var (role, damage) in new[] { (WonderlandMonsterRole.AthenianMarshal, 17365u),
                     (WonderlandMonsterRole.DemonicAssaulter, 3380u) })
        {
            var spawn = plan.First(p => p.Role == role);
            var baseline = WonderlandMonsterProfilePolicy.Resolve(MonsterCombatProfileCatalog.Resolve(
                (uint)spawn.Stats.Level, MonsterAttackDamageKind.Physical), spawn);
            var calibrated = WonderlandCapturedAttackPolicy.ApplyProfile(baseline, spawn, HostileStatusControlFlags.None);
            var oldTarget = target with { Level = 139, PhysicalDefense = 4566, MagicDefense = 2630,
                PhysicalFlatAbsorption = 3995, MagicFlatAbsorption = 3995 };
            Check.Equal(damage, AuthoredCombatPveCurrent.ResolveBasicAttackForOutcome(calibrated.ToAttackerStats(),
                oldTarget, CombatHitOutcome.Normal).Damage, "earlier capture uses its own mitigation reference");
        }
        var rooster = plan.Single(p => p.Role == WonderlandMonsterRole.FlameRooster);
        var roosterProfile = WonderlandMonsterProfilePolicy.Resolve(MonsterCombatProfileCatalog.Resolve(200,
            MonsterAttackDamageKind.Magical), rooster);
        Check.True(WonderlandCapturedAttackPolicy.ApplyProfile(roosterProfile, rooster, silence) == roosterProfile &&
            WonderlandCapturedAttackPolicy.ApplyProfile(roosterProfile, rooster, HostileStatusControlFlags.None) == roosterProfile,
            "the explicit island3 boss exception preserves its existing attack ratings and damage channel");
        Check.True(WonderlandCapturedAttackPolicy.PrimarySkillId("B_normale_stub_003") == 0 &&
            WonderlandCapturedAttackPolicy.PrimarySkillId("B_norale_Tower_001") == 2000,
            "unobserved Firing Hoop attacks stay disabled and Lost Tower retains its prior generic binding");
    }
}
