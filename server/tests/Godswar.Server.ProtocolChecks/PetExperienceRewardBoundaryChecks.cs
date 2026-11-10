namespace Godswar.Server.ProtocolChecks;

internal static class PetExperienceRewardBoundaryChecks
{
    public static Task RunAsync()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "Godswar.Server",
            "Game",
            "GameClientHandler.Progression.cs"));
        var medusaOverride = source.IndexOf(
            "basePetExperience = medusaRule.PetExperience;",
            StringComparison.Ordinal);
        var petOnlyEligibility = source.IndexOf(
            "basePetExperience > 0",
            StringComparison.Ordinal);
        var applyPetBoost = source.IndexOf(
            "var awardedPetExperience = rewardPolicy.ApplyExperienceMultipliers(",
            StringComparison.Ordinal);
        var settleReward = source.IndexOf(
            "SettleMonsterRewardWithImmediateRetryAsync(",
            StringComparison.Ordinal);

        Check.True(
            medusaOverride >= 0 &&
            petOnlyEligibility > medusaOverride &&
            applyPetBoost > petOnlyEligibility &&
            settleReward > applyPetBoost,
            "pet-only rewards resolve boosts after the Medusa override and " +
            "apply them before durable settlement");
        return Task.CompletedTask;
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(
                    directory.FullName,
                    "GodswarServer.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            "Could not locate the Godswar repository root.");
    }
}
