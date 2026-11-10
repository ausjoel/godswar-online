using Godswar.Server.State;
using Godswar.Server.World.Components.Combat;

namespace Godswar.Server.World.Systems.Combat;

/// <summary>
/// Frozen version-3 Flame capture interpretation retained for evidence replay.
/// Current player skill damage uses the shared version-4 formula.
/// </summary>
internal static class CapturedFlameBlastV3
{
    public const int CapturedFlameBlastVersion = 3;
    private const int Scale = AuthoredCombatFormula.BasisPointScale;

    public static CombatResolution Resolve(
        in CombatAttackerStats attacker, in CombatTargetStats target,
        in SkillCombatDefinition skill, ulong eventId, int targetOrder = 0) =>
        Resolve(attacker, target, Inputs.From(skill), eventId, targetOrder);

    public static CombatResolution Resolve(
        in CombatAttackerStats attacker, in CombatTargetStats target,
        in PlayerCombatSkillSnapshot skill, ulong eventId, int targetOrder = 0) =>
        Resolve(attacker, target, Inputs.From(skill), eventId, targetOrder);

    public static uint Preview(
        in CombatAttackerStats attacker, in SkillCombatDefinition skill) =>
        Preview(attacker, Inputs.From(skill));

    public static uint Preview(
        in CombatAttackerStats attacker, in PlayerCombatSkillSnapshot skill) =>
        Preview(attacker, Inputs.From(skill));

    private static CombatResolution Resolve(
        in CombatAttackerStats attacker, in CombatTargetStats target,
        in Inputs skill, ulong eventId, int targetOrder)
    {
        if (!skill.UsesCapturedFlameBlast)
            return AuthoredCombatPveCurrent.ResolveSkillDamage(attacker, target,
                skill.Property, skill.Power1, skill.Power2, eventId, targetOrder);

        ArgumentOutOfRangeException.ThrowIfNegative(targetOrder);
        // The capture cannot distinguish misses from empty/lost ground targets.
        // Preserve the existing Hit/Dodge decision; only landed damage changes.
        var chance = AuthoredCombatV1.CalculateHitChanceBasisPoints(attacker, target);
        var roll = DeterministicCombatRandom.RollBasisPoints(
            eventId, targetOrder, CombatRandomStage.Hit);
        var rolls = new CombatRollEvidence(chance, roll, Scale, CombatRollEvidence.NotRolled);
        return ResolveCapturedFlame(attacker, target, skill, eventId, targetOrder,
            roll < chance, rolls);
    }

    private static uint Preview(in CombatAttackerStats attacker, in Inputs skill) =>
        skill.UsesCapturedFlameBlast
            ? ResolveCapturedFlame(attacker, default, skill, 0, 0, true,
                new(Scale, CombatRollEvidence.NotRolled, Scale, CombatRollEvidence.NotRolled)).Damage
            : AuthoredCombatPveCurrent.ResolveSkillDamageForOutcome(attacker, default,
                skill.Property, skill.Power1, skill.Power2, CombatHitOutcome.Normal).Damage;

    private static CombatResolution ResolveCapturedFlame(
        in CombatAttackerStats attacker, in CombatTargetStats target, in Inputs skill,
        ulong eventId, int targetOrder, bool hit, in CombatRollEvidence rolls)
    {
        var attack = Math.Max(0, attacker.MagicAttack);
        if (!hit)
            return new(CapturedFlameBlastVersion, eventId, targetOrder,
                CombatDamageChannel.Magic, CombatHitOutcome.Miss, 0, rolls,
                new(attack, 0, attack, 0, 0, 0, 0, 0, 0, 0));

        // Captured rank V: same damage on level 1/2 creatures and Wonderland
        // targets. No subtraction of ordinary magic defense or separate magical
        // append fits the observed Merge and Petbird transitions.
        // Keep encounter-specific reductions/absorption below as explicit rules.
        var core = attack + (attack + skill.AuthoredPower2) * 0.5m
            + skill.ZodiacFlatRank * 95m;
        var typed = core * (1m + Math.Max(0, attacker.MagicDamageBonusBasisPoints) / (decimal)Scale);

        // All captured landed Flame V results have critical-style presentation
        // and this damage curve. This is a fixed skill profile, not a recovered
        // universal critical chance rule for other spells or PvP.
        var criticalTotal = typed * 1.5m
            * (1m + Math.Max(0, attacker.CriticalDamageBasisPoints) / (decimal)Scale)
            + Math.Max(0, attacker.CriticalDamageFlat);
        var criticalBonus = Math.Max(0m, (criticalTotal - typed)
            * (1m - ClampReduction(target.CriticalDamageReductionBasisPoints))
            - Math.Max(0, target.CriticalDamageFlatReduction));
        var total = typed + criticalBonus;
        var reduced = total * (1m - ClampReduction(target.MagicDamageReductionBasisPoints));
        var increased = reduced * (1m + Math.Clamp(target.MagicDamageTakenIncreaseBasisPoints,
            0, AuthoredCombatFormula.MaximumDamageTakenIncreaseBasisPoints) / (decimal)Scale);
        var absorbed = increased - Math.Max(0, target.MagicFlatAbsorption);
        var rounded = decimal.Round(absorbed, 0, MidpointRounding.AwayFromZero);
        var damage = (uint)Math.Clamp(rounded, total > 0 ? 1m : 0m, uint.MaxValue);
        return new(CapturedFlameBlastVersion, eventId, targetOrder,
            CombatDamageChannel.Magic, CombatHitOutcome.Critical, damage, rolls,
            new(attack, 0, attack, core, typed, criticalBonus, total, reduced, increased, absorbed));
    }

    private static decimal ClampReduction(int value) =>
        Math.Clamp(value, 0, AuthoredCombatFormula.MaximumDamageReductionBasisPoints) / (decimal)Scale;

    private readonly record struct Inputs(
        uint SkillId, int Property, decimal Power1, decimal Power2,
        decimal ZodiacFlatPower, int ZodiacFlatRank,
        int Target, int Affect, float Range)
    {
        public decimal AuthoredPower2 => Power2 - ZodiacFlatPower;

        // Pin the observed content, not just the ID. Other ranks and altered
        // definitions need their own evidence before selecting this profile.
        public bool UsesCapturedFlameBlast =>
            SkillId == 574 && Property == 1 && Power1 == -0.5m &&
            Target == 63 && Affect == 28 && Range == 4f &&
            ZodiacFlatRank is >= 0 and <= 50 && ZodiacFlatPower >= 0m &&
            ZodiacFlatPower <= Power2 && AuthoredPower2 == 180m &&
            // Validate the current projection contract before interpreting its
            // separate rank with the external profile's 95-per-rank table.
            ZodiacFlatPower == ZodiacFlatRank * 100m;

        public static Inputs From(in SkillCombatDefinition skill) =>
            new(unchecked((uint)skill.SkillId), skill.Property, skill.Power1, skill.Power2,
                skill.ZodiacFlatPower, skill.ZodiacFlatRank, skill.Target, skill.AffectObj, skill.Range);

        public static Inputs From(in PlayerCombatSkillSnapshot skill) =>
            new(skill.SkillId, skill.Property, skill.Power1, skill.Power2,
                skill.ZodiacFlatPower, skill.ZodiacFlatRank, skill.Target, skill.AffectObject, skill.AreaRadius);
    }
}
