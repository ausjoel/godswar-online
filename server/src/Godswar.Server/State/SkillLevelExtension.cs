using System.Collections.Frozen;

namespace Godswar.Server.State;

/// <summary>
/// B22 K1 (item 13, the admin d42): player skill levels 6-10 use ids family*10 + 5..9. Their combat numbers (MP, Power2)
/// are published rows (PostgresGameplayContentPublisher.SkillLevels.cs); the hand-written per-skill catalogs
/// (statuses, damage over time, debuffs, stuns, heals) know only levels 1-5, so they resolve a level 6-10 id through
/// its family's level-5 entry with the strength scaled x1.12 per level above 5 (GENERATED rule, SCOPING section 13)
/// and the priority raised by one per level so a higher level replaces a lower one of the same kind.
/// </summary>
internal static class SkillLevelExtension
{
    public const decimal StepMultiplier = 1.12m;

    // The 44 player families (skill_id / 10) that have a level-5 row at x4.
    private static readonly FrozenSet<int> Families = new[]
    {
        0, 1, 2, 3, 4, 5, 6, 7, 8, 9,
        25, 26, 27, 28, 29, 30, 31, 32, 33, 34, 35,
        50, 51, 52, 53, 54, 55, 56, 57, 58, 59, 60,
        75, 76, 77, 78, 79, 80, 81, 82, 83, 84, 85, 86
    }.ToFrozenSet();

    /// <summary>True for a level 6-10 id: <paramref name="levelFiveId"/> = the family's x4 id, steps = level - 5.</summary>
    public static bool TryGetLevelFive(int skillId, out int levelFiveId, out int steps)
    {
        levelFiveId = 0;
        steps = 0;
        if (skillId is < 0 or >= 1_000 || skillId % 10 < 5 || !Families.Contains(skillId / 10))
        {
            return false;
        }

        levelFiveId = skillId / 10 * 10 + 4;
        steps = skillId % 10 - 4;
        return true;
    }

    /// <summary>The last id of a family whose first id is <paramref name="firstSkillId"/> (x9 when extended).</summary>
    public static int LastIdOfFamily(int firstSkillId) =>
        Families.Contains(firstSkillId / 10) ? firstSkillId + 9 : firstSkillId + 4;

    public static decimal Factor(int steps)
    {
        var factor = 1m;
        for (var i = 0; i < steps; i++)
        {
            factor *= StepMultiplier;
        }

        return factor;
    }

    public static int Scale(int value, int steps) =>
        (int)Math.Clamp(Math.Round(value * Factor(steps), MidpointRounding.AwayFromZero), int.MinValue, int.MaxValue);

    /// <summary>Damage reductions (0..1) scale too but stop at 0.5 so a buff never halves more than half.</summary>
    public static decimal ScaleFraction(decimal value, int steps) =>
        value <= 0m ? value : Math.Max(value, Math.Min(0.5m, Math.Round(value * Factor(steps), 4)));

    public static SkillStatusEffectDefinition Scale(SkillStatusEffectDefinition b, int skillId, int steps) =>
        b with
        {
            SkillId = skillId,
            Priority = b.Priority + steps,
            HitBonus = Scale(b.HitBonus, steps),
            CriticalAppendBonus = Scale(b.CriticalAppendBonus, steps),
            PhysicalDamageReduction = ScaleFraction(b.PhysicalDamageReduction, steps),
            MagicDamageReduction = ScaleFraction(b.MagicDamageReduction, steps),
            PhysicalDefenseBonus = Scale(b.PhysicalDefenseBonus, steps),
            MagicDefenseBonus = Scale(b.MagicDefenseBonus, steps),
            DodgeBonus = Scale(b.DodgeBonus, steps),
            CriticalResistanceBonus = Scale(b.CriticalResistanceBonus, steps),
            PhysicalAttackBonus = Scale(b.PhysicalAttackBonus, steps),
            MagicAttackBonus = Scale(b.MagicAttackBonus, steps),
            PhysicalDamageBonusBasisPoints = Scale(b.PhysicalDamageBonusBasisPoints, steps),
            MagicDamageBonusBasisPoints = Scale(b.MagicDamageBonusBasisPoints, steps),
            HpPerSecond = Scale(b.HpPerSecond, steps),
            MpPerSecond = Scale(b.MpPerSecond, steps),
            PhysicalDamageTakenIncreaseBasisPoints = Scale(b.PhysicalDamageTakenIncreaseBasisPoints, steps),
            MagicDamageTakenIncreaseBasisPoints = Scale(b.MagicDamageTakenIncreaseBasisPoints, steps)
        };
}
