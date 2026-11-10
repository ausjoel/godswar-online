namespace Godswar.Server.Application.WorldInstances;

internal readonly record struct MedusaCompletionRewardAward(
    int HardPoints,
    MedusaEncounterTitleAward? Title,
    string NotificationText)
{
    public uint AwardedTitleId => Title is { } title
        ? MedusaTitleAwardPolicy.GetClientTitleId(title.Title)
        : 0;
}

/// <summary>
/// Live reward policy for a run that has ended, whether it defeated both final
/// bosses or stopped earlier. A victory pays the documented time tier and its
/// title; a run that ended before the victory score keeps the documented score
/// tier it actually reached, by the floor rule the operators specified, and
/// never a title. The score is never rewritten to 3,000 merely to choose the
/// documented time reward.
/// </summary>
internal static class MedusaCompletionRewardPolicy
{
    public static bool SupportsSettlement(
        MedusaEncounterDifficulty difficulty) =>
        MedusaRewardPolicyCatalog.Current.SupportsDifficulty(difficulty);

    public static bool TryResolve(
        MedusaEncounterDifficulty difficulty,
        bool completed,
        int finalScore,
        TimeSpan elapsed,
        out MedusaCompletionRewardAward award)
    {
        if (finalScore < 0 ||
            elapsed < TimeSpan.Zero ||
            elapsed >= MedusaIslandPolicy.TimeLimit ||
            !SupportsSettlement(difficulty))
        {
            award = default;
            return false;
        }

        // Whether the bosses fell decides the tier family, not the score: a party
        // that killed Medusa inside the clock keeps its documented time tier even
        // when its score never reached the victory threshold - it simply earns no
        // title for it. A run that ended before that keeps the documented score
        // tier it actually reached and never a title.
        if (completed)
        {
            if (!MedusaRewardPolicyCatalog.Current.TryResolveCompleted(
                    difficulty,
                    finalScore,
                    elapsed,
                    out var completedPoints,
                    out var completedTitle))
            {
                award = default;
                return false;
            }

            completedPoints = ScaleHonor(completedPoints); // RATES

            award = new(
                completedPoints,
                completedTitle,
                completedTitle is { } selected
                    ? $"The team has defeated Medusa within " +
                      $"{selected.MaximumCompletionTime.TotalMinutes:0} " +
                      $"minutes and earned the title of " +
                      $"'{selected.DisplayName}'."
                    : "The team has successfully killed Medusa.");
            return true;
        }

        if (!MedusaRewardPolicyCatalog.Current.TryResolve(
                difficulty,
                finalScore,
                elapsed,
                out var partialPoints,
                out _))
        {
            award = default;
            return false;
        }

        partialPoints = ScaleHonor(partialPoints); // RATES

        award = new(
            partialPoints,
            null,
            $"Medusa was not defeated, but the team left the island " +
            $"with {partialPoints} honor.");
        return true;
    }

    // RATES: Medusa honour multiplier (public.server_rates), applied to the
    // database-authored tier value before the award, its notice and its
    // settlement are built.
    private static int ScaleHonor(int honorPoints) =>
        Godswar.Server.Application.Rates.ServerRates.Current.Multiply(
            Godswar.Server.Application.Rates.ServerRateKeys.MedusaHonor,
            honorPoints,
            100_000_000);
}
