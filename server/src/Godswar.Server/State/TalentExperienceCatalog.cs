namespace Godswar.Server.State;

internal static class TalentExperienceCatalog
{
    public const int ExperiencePerPoint = 100;
    public const int MaximumRemainder = ExperiencePerPoint - 1;

    public static TalentExperienceProgression Apply(
        int currentExperience, int currentPoints, int gainedExperience)
    {
        if (currentExperience is < 0 or > MaximumRemainder)
        {
            throw new ArgumentOutOfRangeException(nameof(currentExperience));
        }
        ArgumentOutOfRangeException.ThrowIfNegative(currentPoints);
        ArgumentOutOfRangeException.ThrowIfNegative(gainedExperience);

        // A full point balance must not reject the surrounding death commit.
        // Credit only the remaining representable value, including its EXP bar.
        var capacity = ((long)int.MaxValue - currentPoints) * ExperiencePerPoint +
            MaximumRemainder - currentExperience;
        var credited = checked((int)Math.Min(gainedExperience, capacity));
        var accumulated = (long)currentExperience + credited;
        var pointsGained = checked((int)(accumulated / ExperiencePerPoint));
        return new(
            checked((int)(accumulated % ExperiencePerPoint)),
            checked(currentPoints + pointsGained),
            credited,
            pointsGained);
    }
}

internal readonly record struct TalentExperienceProgression(
    int Experience, int Points, int ExperienceGained, int PointsGained);
