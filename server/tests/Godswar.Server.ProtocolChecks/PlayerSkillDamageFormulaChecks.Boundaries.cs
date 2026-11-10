using Godswar.Server.Game;
using Godswar.Server.State;
using Godswar.Server.World.Systems.Combat;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PlayerSkillDamageFormulaChecks
{
    private static void CheckExplicitMitigation()
    {
        var attacker = Attacker(1000);
        var target = new CombatTargetStats
        {
            CriticalDamageReductionBasisPoints = 2000,
            CriticalDamageFlatReduction = 10,
            MagicDamageReductionBasisPoints = 2500,
            MagicDamageTakenIncreaseBasisPoints = 1000,
            MagicFlatAbsorption = 50
        };
        var result = Landed(attacker, target, Flame(0));
        // Baseline core1590; critical bonus795*.8-10=626; total2216.
        Check.Equal(1590m, result.Evidence.SkillCoreDamage, "authored flat180 is inside the half-power term");
        Check.Equal(626m, result.Evidence.CriticalBonusDamage, "critical reductions affect only the bonus");
        Check.Equal(2216m, result.Evidence.DamageWithAppend, "typed base survives bonus reduction");
        Check.Equal(1662m, result.Evidence.DamageAfterReduction, "explicit encounter25% reduction is retained");
        Check.Equal(1828.2m, result.Evidence.DamageAfterTakenIncrease, "explicit10% vulnerability follows reduction");
        Check.Equal(1778.2m, result.Evidence.DamageAfterAbsorption, "flat absorption follows multipliers");
        Check.Equal(1778u, result.Damage, "mitigated result rounds only at the boundary");
        Check.Equal(1u, Landed(attacker, target with { MagicFlatAbsorption = int.MaxValue }, Flame(0)).Damage,
            "positive landed damage retains its minimum-one floor");
        var clamped = Landed(attacker, target with
        {
            CriticalDamageReductionBasisPoints = int.MaxValue,
            MagicDamageReductionBasisPoints = int.MaxValue,
            MagicDamageTakenIncreaseBasisPoints = int.MaxValue
        }, Flame(0));
        var explicitCaps = Landed(attacker, target with
        {
            CriticalDamageReductionBasisPoints = 8000,
            MagicDamageReductionBasisPoints = 8000,
            MagicDamageTakenIncreaseBasisPoints = 8000
        }, Flame(0));
        Check.Equal(explicitCaps, clamped, "existing explicit mitigation caps remain bounded");
    }

    private static void AssertFallback(in CombatAttackerStats attacker, in CombatTargetStats target,
        in SkillCombatDefinition skill, string reason)
    {
        var snapshot = skill.SkillId < 0 ? default : TrainingDummyDamageSkillPolicy.Snapshot(skill);
        if (skill.SkillId < 0)
        {
            var invalidSkill = skill;
            Check.Throws<OverflowException>(() => TrainingDummyDamageSkillPolicy.Snapshot(invalidSkill),
                "negative legacy skill IDs cannot cross the unsigned ECS snapshot boundary");
        }
        for (ulong eventId = 1; eventId <= 16; eventId++)
        {
            var expected = AuthoredCombatV1.ResolveSkillDamage(attacker, target,
                skill.Property, skill.Power1, skill.Power2, eventId, 1);
            Check.Equal(expected, CapturedFlameBlastV3.Resolve(attacker, target, skill, eventId, 1),
                reason + " returns exactV1 decision and evidence");
            if (skill.SkillId >= 0)
                Check.Equal(expected, CapturedFlameBlastV3.Resolve(attacker, target, snapshot, eventId, 1),
                    reason + " returns exactV1 through ECS");
        }
        Check.Equal(AuthoredCombatV1.ResolveSkillDamageForOutcome(attacker, default,
                skill.Property, skill.Power1, skill.Power2, CombatHitOutcome.Normal).Damage,
            CapturedFlameBlastV3.Preview(attacker, skill), reason + " retains historical normal preview");
    }

    private static void CheckBoundedInputs()
    {
        var skill = Flame(50);
        var maximum = Attacker(int.MaxValue, int.MaxValue, int.MaxValue, int.MaxValue);
        Check.Equal(uint.MaxValue, Landed(maximum, default, skill).Damage,
            "maximal integer combat channels saturate at nativeuint instead of overflowing");
        var negative = Attacker(int.MinValue, int.MinValue, int.MinValue, int.MinValue);
        Check.Equal(135u, Landed(negative, default, Flame(0)).Damage,
            "negative actor channels clamp to zero while genuine authored flat damage remains");
        var normal = Attacker(1000);
        Check.Equal(Landed(normal, default, Flame(0)), Landed(normal, new()
        {
            MagicDamageReductionBasisPoints = int.MinValue,
            MagicDamageTakenIncreaseBasisPoints = int.MinValue,
            CriticalDamageReductionBasisPoints = int.MinValue,
            CriticalDamageFlatReduction = int.MinValue, MagicFlatAbsorption = int.MinValue
        }, Flame(0)), "negative defensive inputs cannot increase damage");
        Check.Throws<ArgumentOutOfRangeException>(() =>
            CapturedFlameBlastV3.Resolve(normal, default, Flame(0), 1, -1),
            "negative target order is rejected for captured profile");
        AssertFallback(normal, default, Flame(0) with { Power2 = decimal.MaxValue },
            "maximal altered content retains saturating historical fallback");
        AssertFallback(normal, default, Flame(0) with { Power2 = decimal.MinValue },
            "negative altered content retains historical fallback");
    }
}
