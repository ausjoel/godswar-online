namespace Godswar.Server.State;

/// <summary>
/// The tier rule both experience-potion families apply to a new grant: a
/// strictly higher tier replaces the active grant and drops its remaining time,
/// the same tier only extends the timer up to the caller's ceiling, and a lower
/// tier changes nothing.
/// </summary>
internal static class ExperienceBoostTierRule
{
    public static ExperienceBoostPotionGrant Resolve(
        int activeBonusBasisPoints,
        long activeOnlineTicks,
        int incomingStatusId,
        int incomingSkillId,
        int incomingBonusBasisPoints,
        long incomingOnlineTicks,
        long maximumOnlineTicks)
    {
        if (activeBonusBasisPoints > incomingBonusBasisPoints)
        {
            return new(
                ReplacesActive: false,
                StatusId: incomingStatusId,
                SkillId: incomingSkillId,
                BonusBasisPoints: activeBonusBasisPoints,
                OnlineTicks: activeOnlineTicks);
        }

        if (activeBonusBasisPoints == incomingBonusBasisPoints)
        {
            return new(
                ReplacesActive: true,
                StatusId: incomingStatusId,
                SkillId: incomingSkillId,
                BonusBasisPoints: incomingBonusBasisPoints,
                OnlineTicks: Math.Min(
                    maximumOnlineTicks,
                    Math.Max(0, activeOnlineTicks) + incomingOnlineTicks));
        }

        return new(
            ReplacesActive: true,
            StatusId: incomingStatusId,
            SkillId: incomingSkillId,
            BonusBasisPoints: incomingBonusBasisPoints,
            OnlineTicks: incomingOnlineTicks);
    }
}
