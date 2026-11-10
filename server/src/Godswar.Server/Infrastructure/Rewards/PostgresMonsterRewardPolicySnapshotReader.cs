using Godswar.Server.Application.Rewards;
using Npgsql;

namespace Godswar.Server.Infrastructure.Rewards;

internal static class PostgresMonsterRewardPolicySnapshotReader
{
    public static async Task<MonsterRewardPolicySnapshot> LoadAsync(
        string connectionString,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        await using var dataSource = NpgsqlDataSource.Create(
            connectionString);
        return await LoadAsync(dataSource, cancellationToken);
    }

    internal static async Task<MonsterRewardPolicySnapshot> LoadAsync(
        NpgsqlDataSource dataSource,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        await using var command = dataSource.CreateCommand(
            """
            SELECT setting_id,
                   maximum_lower_level_gap,
                   global_experience_multiplier_basis_points,
                   revision,
                   updated_at,
                   updated_by
            FROM public.monster_reward_settings
            ORDER BY setting_id;
            """);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken) ||
            reader.GetInt16(0) != 1)
        {
            throw new InvalidDataException(
                "The monster-reward policy singleton is missing.");
        }

        var snapshot = new MonsterRewardPolicySnapshot(
            reader.GetInt16(1),
            reader.GetInt32(2),
            reader.GetInt64(3),
            new DateTimeOffset(reader.GetDateTime(4).ToUniversalTime()),
            reader.GetString(5));
        if (await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidDataException(
                "The monster-reward policy singleton is ambiguous.");
        }

        snapshot.Validate();
        return snapshot;
    }
}
