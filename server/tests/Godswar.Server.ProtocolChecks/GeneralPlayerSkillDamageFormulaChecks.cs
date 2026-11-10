using Godswar.Server.Game;
using Godswar.Server.State;
using Godswar.Server.World.Components.Combat;
using Godswar.Server.World.Systems.Combat;

namespace Godswar.Server.ProtocolChecks;

internal static class GeneralPlayerSkillDamageFormulaChecks
{
    public const string CheckName = "General V5 player skill damage across classes, ranks and combat contexts";

    public static Task RunAsync()
    {
        CheckPublishedRankEndpoints();
        CheckEveryPublishedDamagingDefinition();
        CheckTypedMitigationAndTraining();
        CheckChancePoliciesAndSnapshots();
        CheckNonDamageClassification();
        return Task.CompletedTask;
    }

    private static void CheckPublishedRankEndpoints()
    {
        // Independent decimal endpoints: attack1000 - effective defense200.
        // Core=800+(800+authored flat)*coefficient. Normal=core*1.1+17;
        // critical=core*1.1*1.5*1.2+30+17. Round only at the final boundary.
        var rows = new (byte Class, int Id, decimal Power1, decimal Flat,
            decimal Core, uint Normal, uint Critical)[]
        {
            (0, 0, -.5m, 250m, 1325m, 1475u, 2671u),
            (0, 1, -.4m, 400m, 1520m, 1689u, 3057u),
            (0, 2, -.2m, 530m, 1864m, 2067u, 3738u),
            (0, 3, 0m, 900m, 2500m, 2767u, 4997u),
            (0, 4, .05m, 990m, 2679.5m, 2964u, 5352u),
            (1, 290, .4m, 700m, 2900m, 3207u, 5789u),
            (1, 291, .8m, 1000m, 4040m, 4461u, 8046u),
            (1, 292, 1.5m, 1800m, 7300m, 8047u, 14501u),
            (1, 293, 2.4m, 3200m, 14400m, 15857u, 28559u),
            (1, 294, 2.64m, 3520m, 16524.8m, 18194u, 32766u),
            (2, 800, -.5m, 200m, 1300m, 1447u, 2621u),
            (2, 801, -.5m, 330m, 1365m, 1519u, 2750u),
            (2, 802, -.5m, 460m, 1430m, 1590u, 2878u),
            (2, 803, -.5m, 800m, 1600m, 1777u, 3215u),
            (2, 804, -.4m, 960m, 1856m, 2059u, 3722u),
            (3, 500, -.3m, 70m, 1409m, 1567u, 2837u),
            (3, 501, -.3m, 120m, 1444m, 1605u, 2906u),
            (3, 502, -.2m, 200m, 1600m, 1777u, 3215u),
            (3, 503, -.1m, 280m, 1772m, 1966u, 3556u),
            (3, 504, -.08m, 336m, 1845.12m, 2047u, 3700u),
            (3, 570, -.8m, 24m, 964.8m, 1078u, 1957u),
            (3, 571, -.8m, 52m, 970.4m, 1084u, 1968u),
            (3, 572, -.6m, 86m, 1154.4m, 1287u, 2333u),
            (3, 573, -.6m, 124m, 1169.6m, 1304u, 2363u),
            (3, 574, -.5m, 180m, 1290m, 1436u, 2601u)
        };
        foreach (var row in rows)
        {
            Check.True(GameplayContentTestFixtures.Runtime.SkillCombat.TryGet(row.Id, out var skill),
                $"published rank{row.Id} exists");
            Check.True(skill.Power1 == row.Power1 && skill.AuthoredPower2 == row.Flat &&
                GameplayContentTestFixtures.Published.SkillCombatDefinitions.Single(x => x.SkillId == row.Id)
                    .ClassIds.Contains((short)row.Class), $"rank{row.Id} retains its published owner and coefficients");
            var attacker = Attacker(row.Class);
            var target = new CombatTargetStats { PhysicalDefense = 250, MagicDefense = 250 };
            foreach (var pvp in new[] { false, true })
            {
                var normal = PlayerSkillDamageFormula.ResolveForOutcome(attacker, target, skill,
                    CombatHitOutcome.Normal, pvp);
                var critical = PlayerSkillDamageFormula.ResolveForOutcome(attacker, target, skill,
                    CombatHitOutcome.Critical, pvp);
                var miss = PlayerSkillDamageFormula.ResolveForOutcome(attacker, target, skill,
                    CombatHitOutcome.Miss, pvp);
                Check.Equal(row.Core, normal.Evidence.SkillCoreDamage, $"rank{row.Id} includes base plus additional");
                Check.Equal(row.Normal, normal.Damage, $"rank{row.Id} normal endpoint pvp={pvp}");
                Check.Equal(row.Critical, critical.Damage, $"rank{row.Id} critical endpoint pvp={pvp}");
                Check.True(normal.FormulaVersion == PlayerSkillDamageFormula.GeneralVersion && !normal.IsCritical && critical.IsCritical &&
                    miss.FormulaVersion == PlayerSkillDamageFormula.GeneralVersion && miss.Damage == 0 && miss.CapturedDamageValue == uint.MaxValue,
                    "V4 damage preserves distinct normal, critical and miss outcomes");
                foreach (var outcome in new[] { CombatHitOutcome.Normal, CombatHitOutcome.Critical })
                {
                    var withoutAppend = PlayerSkillDamageFormula.ResolveForOutcome(attacker with
                    { PhysicalAppendDamage = 0, MagicAppendDamage = 0 }, target, skill, outcome, pvp);
                    var actual = outcome == CombatHitOutcome.Normal ? normal : critical;
                    Check.Equal(17u, actual.Damage - withoutAppend.Damage,
                        "fixed typed append is neither lost nor multiplied by the critical factor");
                }
            }
        }
    }

    private static void CheckEveryPublishedDamagingDefinition()
    {
        var definitions = 0;
        var professions = new HashSet<short>();
        foreach (var published in GameplayContentTestFixtures.Published.SkillCombatDefinitions)
        {
            Check.True(GameplayContentTestFixtures.Runtime.SkillCombat.TryGet(published.SkillId, out var skill),
                "every published player skill has a runtime combat definition");
            if (!PlayerSkillDamageEligibility.IsDamaging(skill)) continue;
            definitions++;
            foreach (var profession in published.ClassIds)
            {
                professions.Add(profession);
                var attacker = Attacker(checked((byte)profession));
                var snapshot = PlayerCombatEcsRequest.HostileSkill(PlayerCombatIntentKind.SingleTargetSkill,
                    DateTimeOffset.UnixEpoch, 1, skill).Skill;
                foreach (var pvp in new[] { false, true })
                {
                    var result = PlayerSkillDamageFormula.ResolveForOutcome(attacker, default, skill,
                        CombatHitOutcome.Normal, pvp);
                    Check.Equal(PlayerSkillDamageFormula.GeneralVersion, result.FormulaVersion,
                        $"published DPS{skill.SkillId} class{profession} pvp={pvp} cannot silently fall back to an old formula");
                    Check.Equal(result, PlayerSkillDamageFormula.ResolveForOutcome(attacker, default, snapshot,
                        CombatHitOutcome.Normal, pvp), "every authored rank retains exact scalar snapshot parity");
                }
            }
        }
        Check.True(definitions >= 25 && professions.SetEquals(new short[] { 0, 1, 2, 3 }),
            "catalog-wide eligibility covers the admitted damaging ranks of all four classes");
    }

    private static void CheckTypedMitigationAndTraining()
    {
        var attacker = Attacker(3) with
        {
            PhysicalAttack = 900, MagicAttack = 1500,
            IgnorePhysicalDefenseBasisPoints = 5000, IgnoreMagicDefenseBasisPoints = 2000,
            PhysicalDamageBonusBasisPoints = 2500, MagicDamageBonusBasisPoints = 5000,
            PhysicalAppendDamage = 11, MagicAppendDamage = 17
        };
        var target = new CombatTargetStats
        {
            PhysicalDefense = 200, MagicDefense = 250,
            PhysicalDamageReductionBasisPoints = 2000, MagicDamageReductionBasisPoints = 3000,
            PhysicalDamageTakenIncreaseBasisPoints = 1000, MagicDamageTakenIncreaseBasisPoints = 2000,
            PhysicalFlatAbsorption = 23, MagicFlatAbsorption = 29,
            CriticalDamageReductionBasisPoints = 2500, CriticalDamageFlatReduction = 20
        };
        foreach (var (property, core, normal, critical) in new[]
            { (0, 2490m, 2726u, 4371u), (1, 3740m, 4698u, 7527u) })
        {
            var skill = new SkillCombatDefinition(500, 44, 28, 11, 0, property, 10,
                .5m, 390m, ZodiacFlatPower: 190m, ZodiacFlatRank: 2);
            var n = PlayerSkillDamageFormula.ResolveForOutcome(attacker, target, skill, CombatHitOutcome.Normal);
            var c = PlayerSkillDamageFormula.ResolveForOutcome(attacker, target, skill, CombatHitOutcome.Critical);
            Check.True(n.Channel == (property == 1 ? CombatDamageChannel.Magic : CombatDamageChannel.Physical),
                "skill property selects the channel even when it differs from the caster's basic-attack channel");
            Check.Equal(core, n.Evidence.SkillCoreDamage, "two training ranks add190 after authored skill scaling");
            Check.Equal(normal, n.Damage, "typed normal modifiers follow defense, core, bonus, append, reduction and absorption");
            Check.Equal(critical, c.Damage, "critical cancellation affects only the bonus before append and typed mitigation");
        }
    }

    private static void CheckChancePoliciesAndSnapshots()
    {
        foreach (byte profession in new byte[] { 0, 1, 2, 3 })
        foreach (var pvp in new[] { false, true })
        {
            var attacker = Attacker(profession) with { Hit = 500, Critical = 600 };
            var target = new CombatTargetStats { Level = 100, Dodge = 400, CriticalResistance = 300 };
            var skill = new SkillCombatDefinition(574, 63, 28, 11, 4, profession < 2 ? 0 : 1, 180, -.5m, 180m);
            var snapshot = PlayerCombatEcsRequest.HostileSkill(PlayerCombatIntentKind.AreaSkill,
                DateTimeOffset.UnixEpoch, uint.MaxValue, skill).Skill;
            var outcomes = new HashSet<CombatHitOutcome>();
            for (ulong id = 1; id <= 128; id++)
            {
                var previous = pvp
                    ? AuthoredCombatV2.ResolveSkillDamage(attacker, target, skill.Property, skill.Power1, skill.Power2, id)
                    : AuthoredPlayerPveCurrent.ResolveSkillDamage(attacker, target, skill.Property, skill.Power1, skill.Power2, id);
                var actual = PlayerSkillDamageFormula.Resolve(attacker, target, skill, id, pvp: pvp);
                var ecs = PlayerSkillDamageFormula.Resolve(attacker, target, snapshot, id, pvp: pvp);
                Check.True(actual.Outcome == previous.Outcome && actual.Rolls == previous.Rolls,
                    "V5 uses each context's accuracy and the shared player critical policy");
                Check.Equal(actual, ecs, "Legacy/ECS scalar adapters carry identical V5 evidence and deterministic results");
                Check.Equal(actual, PlayerSkillDamageFormula.Resolve(attacker, target, skill, id, pvp: pvp),
                    "the same admitted event replays deterministically");
                outcomes.Add(actual.Outcome);
            }
            Check.Equal(3, outcomes.Count, "each combat context still exercises normal, critical and miss outcomes");
        }
    }

    private static void CheckNonDamageClassification()
    {
        var attacker = Attacker(3) with { PhysicalAppendDamage = 10000, MagicAppendDamage = 10000 };
        var control = new SkillCombatDefinition(600, 44, 28, 11, 0, 1, 1, -1m, 0m);
        var trainedControl = control with { Power1 = .2m, Power2 = 200m,
            ZodiacPowerAdjustment = 1.2m, ZodiacFlatPower = 200m, ZodiacFlatRank = 2 };
        foreach (var skill in new[] { control, trainedControl })
        {
            Check.True(!PlayerSkillDamageEligibility.IsDamaging(skill), "training cannot turn native Silence into DPS");
            foreach (var pvp in new[] { false, true })
            foreach (var outcome in new[] { CombatHitOutcome.Normal, CombatHitOutcome.Critical, CombatHitOutcome.Miss })
                Check.Equal(0u, PlayerSkillDamageFormula.ResolveForOutcome(attacker, default, skill, outcome, pvp).Damage,
                    "hostile control has no base-attack, training or append damage leakage");
        }
        foreach (var id in new[] { 750, 760, 770 })
        {
            Check.True(GameplayContentTestFixtures.Runtime.SkillCombat.TryGet(id, out var skill), "native support skill exists");
            Check.True(!PlayerSkillDamageEligibility.IsDamaging(skill), "Heal, Area Heal and Gaia Care remain outside player DPS");
        }
    }

    private static CombatAttackerStats Attacker(byte profession) => new()
    {
        Level = 100, Profession = profession, PhysicalAttack = 1000, MagicAttack = 1000,
        PhysicalDamageBonusBasisPoints = 1000, MagicDamageBonusBasisPoints = 1000,
        IgnorePhysicalDefenseBasisPoints = 2000, IgnoreMagicDefenseBasisPoints = 2000,
        CriticalDamageBasisPoints = 2000, CriticalDamageFlat = 30,
        PhysicalAppendDamage = 17, MagicAppendDamage = 17
    };
}
