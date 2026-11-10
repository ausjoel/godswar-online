using Godswar.Server.State;
using Godswar.Server.World.Components.Combat;

namespace Godswar.Server.World.Systems.Combat;

/// <summary>
/// User-selected common player damage formula, version 5. Each skill rank's
/// authored powers supply its additional contribution. This generalization is
/// a gameplay rule, not a claim that every external skill has been recovered.
/// </summary>
internal static class PlayerSkillDamageFormula
{
    public const int GeneralVersion = 5;
    private const int Scale = AuthoredCombatFormula.BasisPointScale;

    public static CombatResolution Resolve(
        in CombatAttackerStats attacker, in CombatTargetStats target,
        in SkillCombatDefinition skill, ulong eventId, int targetOrder = 0, bool pvp = false) =>
        Resolve(attacker, target, PlayerSkillDamageInputs.From(skill), eventId, targetOrder, pvp);

    public static CombatResolution Resolve(
        in CombatAttackerStats attacker, in CombatTargetStats target,
        in PlayerCombatSkillSnapshot skill, ulong eventId, int targetOrder = 0, bool pvp = false) =>
        Resolve(attacker, target, PlayerSkillDamageInputs.From(skill), eventId, targetOrder, pvp);

    public static CombatResolution ResolveForOutcome(
        in CombatAttackerStats attacker, in CombatTargetStats target,
        in SkillCombatDefinition skill, CombatHitOutcome outcome, bool pvp = false) =>
        ResolveForOutcome(attacker, target, PlayerSkillDamageInputs.From(skill), outcome, pvp);

    public static CombatResolution ResolveForOutcome(
        in CombatAttackerStats attacker, in CombatTargetStats target,
        in PlayerCombatSkillSnapshot skill, CombatHitOutcome outcome, bool pvp = false) =>
        ResolveForOutcome(attacker, target, PlayerSkillDamageInputs.From(skill), outcome, pvp);

    public static uint Preview(in CombatAttackerStats attacker, in SkillCombatDefinition skill) =>
        ResolveForOutcome(attacker, default, skill, CombatHitOutcome.Normal).Damage;

    public static uint Preview(in CombatAttackerStats attacker, in PlayerCombatSkillSnapshot skill) =>
        ResolveForOutcome(attacker, default, skill, CombatHitOutcome.Normal).Damage;

    private static CombatResolution Resolve(
        in CombatAttackerStats attacker, in CombatTargetStats target,
        in PlayerSkillDamageInputs skill, ulong eventId, int targetOrder, bool pvp)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(targetOrder);
        if (!skill.HasValidProjection || !skill.HasHostileShape)
            return pvp
                ? AuthoredCombatV2.ResolveSkillDamage(attacker, target, skill.Property,
                    skill.Power1, skill.Power2, eventId, targetOrder)
                : AuthoredPlayerPveCurrent.ResolveSkillDamage(attacker, target, skill.Property,
                    skill.Power1, skill.Power2, eventId, targetOrder);

        var chances = Chances(attacker, target, pvp);
        var hitRoll = DeterministicCombatRandom.RollBasisPoints(eventId, targetOrder, CombatRandomStage.Hit);
        if (hitRoll >= chances.Hit)
            return Calculate(attacker, target, skill, eventId, targetOrder, CombatHitOutcome.Miss,
                new(chances.Hit, hitRoll, chances.Critical, CombatRollEvidence.NotRolled));
        var criticalRoll = DeterministicCombatRandom.RollBasisPoints(
            eventId, targetOrder, CombatRandomStage.Critical);
        return Calculate(attacker, target, skill, eventId, targetOrder,
            criticalRoll < chances.Critical ? CombatHitOutcome.Critical : CombatHitOutcome.Normal,
            new(chances.Hit, hitRoll, chances.Critical, criticalRoll));
    }

    private static CombatResolution ResolveForOutcome(
        in CombatAttackerStats attacker, in CombatTargetStats target,
        in PlayerSkillDamageInputs skill, CombatHitOutcome outcome, bool pvp)
    {
        if (outcome is not (CombatHitOutcome.Normal or CombatHitOutcome.Critical or CombatHitOutcome.Miss))
            throw new ArgumentOutOfRangeException(nameof(outcome));
        if (!skill.HasValidProjection || !skill.HasHostileShape)
            return pvp
                ? AuthoredCombatV2.ResolveSkillDamageForOutcome(attacker, target,
                    skill.Property, skill.Power1, skill.Power2, outcome)
                : AuthoredPlayerPveCurrent.ResolveSkillDamageForOutcome(attacker, target,
                    skill.Property, skill.Power1, skill.Power2, outcome);
        var chances = Chances(attacker, target, pvp);
        return Calculate(attacker, target, skill, 0, 0, outcome,
            new(chances.Hit, CombatRollEvidence.NotRolled, chances.Critical, CombatRollEvidence.NotRolled));
    }

    private static (int Hit, int Critical) Chances(
        in CombatAttackerStats attacker, in CombatTargetStats target, bool pvp) => pvp
        ? (AuthoredCombatV2.CalculateHitChanceBasisPoints(attacker, target),
           AuthoredCombatV2.CalculateCriticalChanceBasisPoints(attacker, target))
        : (AuthoredPlayerPveCurrent.CalculateHitChanceBasisPoints(attacker, target),
           AuthoredPlayerPveCurrent.CalculateCriticalChanceBasisPoints(attacker, target));

    private static CombatResolution Calculate(
        in CombatAttackerStats attacker, in CombatTargetStats target, in PlayerSkillDamageInputs skill,
        ulong eventId, int targetOrder, CombatHitOutcome outcome, in CombatRollEvidence rolls)
    {
        var magical = skill.Property == 1;
        var channel = magical ? CombatDamageChannel.Magic : CombatDamageChannel.Physical;
        var attack = Math.Max(0, magical ? attacker.MagicAttack : attacker.PhysicalAttack);
        var defense = AuthoredCombatFormula.CalculateEffectiveDefense(
            magical ? target.MagicDefense : target.PhysicalDefense,
            magical ? attacker.IgnoreMagicDefenseBasisPoints : attacker.IgnorePhysicalDefenseBasisPoints);
        var effectiveAttack = Math.Max(0m, (decimal)attack - defense);
        if (outcome == CombatHitOutcome.Miss || !skill.HasAuthoredDamage)
            return new(GeneralVersion, eventId, targetOrder, channel, outcome, 0, rolls,
                new(attack, defense, effectiveAttack, 0, 0, 0, 0, 0, 0, 0));

        // Power1 stores an adjustment: -0.5 means an additional 50%, not the
        // entire attack. Flat Zodiac training is independent of that multiplier.
        var coefficient = Math.Max(0m, Add(1m, skill.Power1));
        var core = Add(Add(effectiveAttack,
            Multiply(Add(effectiveAttack, skill.AuthoredPower2), coefficient)), skill.ZodiacFlatPower);
        var bonus = magical ? attacker.MagicDamageBonusBasisPoints : attacker.PhysicalDamageBonusBasisPoints;
        var typed = Multiply(core, 1m + Math.Max(0, bonus) / (decimal)Scale);
        var criticalBonus = 0m;
        if (outcome == CombatHitOutcome.Critical)
        {
            var criticalTotal = Add(Multiply(Multiply(typed, 1.5m),
                1m + Math.Max(0, attacker.CriticalDamageBasisPoints) / (decimal)Scale),
                Math.Max(0, attacker.CriticalDamageFlat));
            criticalBonus = Math.Max(0m,
                Multiply(criticalTotal - typed, 1m - Reduction(target.CriticalDamageReductionBasisPoints))
                - Math.Max(0, target.CriticalDamageFlatReduction));
        }
        var append = Math.Max(0, magical ? attacker.MagicAppendDamage : attacker.PhysicalAppendDamage);
        var total = Add(Add(typed, criticalBonus), append);
        var reduced = Multiply(total, 1m - Reduction(magical
            ? target.MagicDamageReductionBasisPoints : target.PhysicalDamageReductionBasisPoints));
        var increase = Math.Clamp(magical ? target.MagicDamageTakenIncreaseBasisPoints
            : target.PhysicalDamageTakenIncreaseBasisPoints, 0,
            AuthoredCombatFormula.MaximumDamageTakenIncreaseBasisPoints);
        var increased = Multiply(reduced, 1m + increase / (decimal)Scale);
        var absorbed = increased - Math.Max(0, magical ? target.MagicFlatAbsorption : target.PhysicalFlatAbsorption);
        var rounded = decimal.Round(absorbed, 0, MidpointRounding.AwayFromZero);
        var damage = (uint)Math.Clamp(rounded, total > 0m ? 1m : 0m, uint.MaxValue);
        return new(GeneralVersion, eventId, targetOrder, channel, outcome, damage, rolls,
            new(attack, defense, effectiveAttack, core, typed, criticalBonus, total, reduced, increased, absorbed));
    }

    private static decimal Reduction(int value) =>
        Math.Clamp(value, 0, AuthoredCombatFormula.MaximumDamageReductionBasisPoints) / (decimal)Scale;

    private static decimal Add(decimal left, decimal right)
    {
        try { return left + right; }
        catch (OverflowException) { return right >= 0 ? decimal.MaxValue : decimal.MinValue; }
    }

    private static decimal Multiply(decimal left, decimal right)
    {
        try { return left * right; }
        catch (OverflowException)
        {
            return Math.Sign(left) == Math.Sign(right) ? decimal.MaxValue : decimal.MinValue;
        }
    }
}
