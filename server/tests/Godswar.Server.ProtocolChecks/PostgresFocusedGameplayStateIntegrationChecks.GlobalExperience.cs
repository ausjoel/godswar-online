using Godswar.Server.Application.Rewards;
using Godswar.Server.Infrastructure.Rewards;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresFocusedGameplayStateIntegrationChecks
{
    private static async Task AssertGlobalExperiencePolicyAsync(
        NpgsqlDataSource dataSource)
    {
        var original =
            await PostgresMonsterRewardPolicySnapshotReader.LoadAsync(
                dataSource);
        var configuredBasisPoints =
            original.GlobalExperienceMultiplierBasisPoints == 23_750
                ? 25_000
                : 23_750;
        try
        {
            await using (var configure = dataSource.CreateCommand(
                """
                UPDATE public.monster_reward_settings
                SET global_experience_multiplier_basis_points = @multiplier,
                    updated_by = 'global-experience-reader-check'
                WHERE setting_id = 1;
                """))
            {
                configure.Parameters.AddWithValue(
                    "multiplier",
                    configuredBasisPoints);
                Check.Equal(
                    1,
                    await configure.ExecuteNonQueryAsync(),
                    "global EXP integration fixture updates the singleton");
            }

            var configured =
                await PostgresMonsterRewardPolicySnapshotReader.LoadAsync(
                    dataSource);
            Check.Equal(
                configuredBasisPoints,
                configured.GlobalExperienceMultiplierBasisPoints,
                "monster-reward reader preserves the configured global EXP multiplier");
            Check.Equal(
                original.Revision + 1,
                configured.Revision,
                "global EXP management updates advance the policy revision");
        }
        finally
        {
            await using var restore = dataSource.CreateCommand(
                """
                UPDATE public.monster_reward_settings
                SET global_experience_multiplier_basis_points = @multiplier,
                    updated_by = @updatedBy
                WHERE setting_id = 1;
                """);
            restore.Parameters.AddWithValue(
                "multiplier",
                original.GlobalExperienceMultiplierBasisPoints);
            restore.Parameters.AddWithValue("updatedBy", original.UpdatedBy);
            await restore.ExecuteNonQueryAsync();
        }
    }
}
