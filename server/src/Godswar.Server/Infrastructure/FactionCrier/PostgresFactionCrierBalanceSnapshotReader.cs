using System.Data;
using Godswar.Server.Application.FactionCrier;
using Npgsql;
using BalanceRewardKind =
    Godswar.Server.Application.FactionCrier.FactionCrierRewardKind;

namespace Godswar.Server.Infrastructure.FactionCrier;

/// <summary>
/// Loads one sealed Faction Crier balance revision. Workers retain this
/// snapshot until a coordinated restart activates a later management publish.
/// </summary>
internal static class PostgresFactionCrierBalanceSnapshotReader
{
    public static async Task<FactionCrierBalanceSnapshot> LoadAsync(
        string connectionString,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        await using var dataSource = NpgsqlDataSource.Create(
            connectionString);
        return await LoadAsync(dataSource, cancellationToken);
    }

    internal static async Task<FactionCrierBalanceSnapshot> LoadAsync(
        NpgsqlDataSource dataSource,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        await using var connection =
            await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction =
            await connection.BeginTransactionAsync(
                IsolationLevel.RepeatableRead,
                cancellationToken);

        var header = await ReadHeaderAsync(
            connection,
            transaction,
            cancellationToken);
        var tiers = await ReadTiersAsync(
            connection,
            transaction,
            header.Revision,
            cancellationToken);
        var options = await ReadOptionsAsync(
            connection,
            transaction,
            header.Revision,
            cancellationToken);
        var snapshot = new FactionCrierBalanceSnapshot(
            header.Revision,
            header.MinimumLevel,
            header.WeeklyReclaimGoldCost,
            header.RenewalGoldCost,
            tiers,
            options);
        snapshot.Validate();
        await transaction.CommitAsync(cancellationToken);
        return snapshot;
    }

    private static async Task<BalanceHeader> ReadHeaderAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT settings.setting_id,
                   revision.revision,
                   revision.minimum_level,
                   revision.weekly_reclaim_gold_cost,
                   revision.renewal_gold_cost,
                   revision.sealed_at IS NOT NULL
            FROM public.faction_crier_balance_settings settings
            JOIN public.faction_crier_balance_revisions revision
              ON revision.revision = settings.revision
            ORDER BY settings.setting_id;
            """,
            connection,
            transaction);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken) ||
            reader.GetInt16(0) != 1 ||
            !reader.GetBoolean(5))
        {
            throw new InvalidDataException(
                "The Faction Crier balance publication is missing or unsealed.");
        }
        var header = new BalanceHeader(
            reader.GetInt64(1),
            reader.GetInt16(2),
            reader.GetInt32(3),
            reader.GetInt32(4));
        if (await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidDataException(
                "The Faction Crier balance publication is ambiguous.");
        }
        return header;
    }

    private static async Task<IReadOnlyList<FactionCrierBalanceTier>>
        ReadTiersAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            long revision,
            CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT minimum_level, maximum_level, base_experience,
                   base_talent_points, triple_silver_cost,
                   all_six_silver_cost
            FROM public.faction_crier_balance_tiers
            WHERE balance_revision = @revision
            ORDER BY minimum_level;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("revision", revision);
        var tiers = new List<FactionCrierBalanceTier>(7);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            tiers.Add(new(
                reader.GetInt16(0),
                reader.GetInt16(1),
                reader.GetInt32(2),
                reader.GetInt32(3),
                reader.GetInt32(4),
                reader.GetInt32(5)));
        }
        return tiers;
    }

    private static async Task<IReadOnlyList<FactionCrierBalanceOption>>
        ReadOptionsAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            long revision,
            CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT sub_id, currency_code, cost, multiplier, reward_kind
            FROM public.faction_crier_balance_options
            WHERE balance_revision = @revision
            ORDER BY sub_id;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("revision", revision);
        var options = new List<FactionCrierBalanceOption>(25);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            options.Add(new(
                reader.GetInt16(0),
                ParseCurrency(reader.GetString(1)),
                reader.GetInt32(2),
                reader.GetInt16(3),
                ParseRewardKind(reader.GetString(4))));
        }
        return options;
    }

    private static FactionCrierCurrency ParseCurrency(string value) =>
        value switch
        {
            "silver" => FactionCrierCurrency.Silver,
            "binding_gold" => FactionCrierCurrency.BoundGold,
            "gold" => FactionCrierCurrency.Gold,
            _ => throw new InvalidDataException(
                $"Unknown Faction Crier currency '{value}'.")
        };

    private static BalanceRewardKind ParseRewardKind(string value) =>
        value switch
        {
            "experience" => BalanceRewardKind.Experience,
            "talent_points" => BalanceRewardKind.TalentPoints,
            "experience_and_talent_points" =>
                BalanceRewardKind.ExperienceAndTalentPoints,
            _ => throw new InvalidDataException(
                $"Unknown Faction Crier reward kind '{value}'.")
        };

    private sealed record BalanceHeader(
        long Revision,
        int MinimumLevel,
        int WeeklyReclaimGoldCost,
        int RenewalGoldCost);
}
