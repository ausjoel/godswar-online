namespace Godswar.Server.State;

/// <summary>
/// Classifies authored hostile damage before Zodiac projection. Projected flat
/// power or percentage training cannot turn a control/heal into a damage skill.
/// </summary>
internal static class PlayerSkillDamageEligibility
{
    public static bool IsDamaging(in SkillCombatDefinition skill) =>
        IsDamaging(skill.Target, skill.AffectObj, skill.Distance, skill.Range,
            skill.Property, skill.AuthoredPower1, skill.AuthoredPower2);

    public static bool IsDamaging(int target, int affect, float distance, float range,
        int property, decimal authoredPower1, decimal authoredPower2)
    {
        if (property is not (0 or 1) ||
            authoredPower1 <= -1m && authoredPower2 <= 0m)
            return false;

        // Match the existing single/self-area/ground-area hostile routing.
        if (target == 44 && affect == 28 && range <= 0f)
            return true;
        if ((affect & 8) == 0 || !(range > 0f))
            return false;
        if ((target & 16) != 0)
            return distance > 0f;
        return (target & 1) != 0;
    }
}
