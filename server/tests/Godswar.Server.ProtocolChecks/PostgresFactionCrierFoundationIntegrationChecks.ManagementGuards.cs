using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresFactionCrierFoundationIntegrationChecks
{
    private static async Task AssertManagementSafetyGuardsAsync(
        NpgsqlDataSource dataSource)
    {
        var activeRevision = await ReadActiveRevisionAsync(dataSource);
        await AssertStockTierShapeGuardAsync(
            dataSource,
            activeRevision);
        await AssertStockOptionShapeGuardAsync(
            dataSource,
            activeRevision);
        await AssertRewardOverflowPublicationGuardAsync(
            dataSource,
            activeRevision);
    }

    private static async Task AssertStockTierShapeGuardAsync(
        NpgsqlDataSource dataSource,
        long activeRevision)
    {
        var candidateRevision = checked(activeRevision + 1);
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await InsertCandidateHeaderAsync(
            connection,
            transaction,
            activeRevision,
            candidateRevision);
        try
        {
            await using var malformed = new NpgsqlCommand(
                """
                INSERT INTO public.faction_crier_balance_tiers (
                    balance_revision, minimum_level, maximum_level,
                    base_experience, base_talent_points,
                    triple_silver_cost, all_six_silver_cost)
                VALUES (@candidateRevision, 20, 40, 1, 1, 0, 0);
                """,
                connection,
                transaction);
            malformed.Parameters.AddWithValue(
                "candidateRevision",
                candidateRevision);
            await malformed.ExecuteNonQueryAsync();
            throw new InvalidOperationException(
                "A Faction Crier revision accepted changed tier bounds.");
        }
        catch (PostgresException exception) when (
            exception.SqlState == PostgresErrorCodes.CheckViolation &&
            exception.ConstraintName ==
                "ck_faction_crier_balance_tier_stock_ranges")
        {
            // Client tier copy is stock-fixed at these seven boundaries.
        }
    }

    private static async Task AssertStockOptionShapeGuardAsync(
        NpgsqlDataSource dataSource,
        long activeRevision)
    {
        var candidateRevision = checked(activeRevision + 1);
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await InsertCandidateHeaderAsync(
            connection,
            transaction,
            activeRevision,
            candidateRevision);
        try
        {
            await using var malformed = new NpgsqlCommand(
                """
                INSERT INTO public.faction_crier_balance_options (
                    balance_revision, sub_id, currency_code, cost,
                    multiplier, reward_kind)
                VALUES (@candidateRevision, 110, 'gold', 0, 6,
                        'experience');
                """,
                connection,
                transaction);
            malformed.Parameters.AddWithValue(
                "candidateRevision",
                candidateRevision);
            await malformed.ExecuteNonQueryAsync();
            throw new InvalidOperationException(
                "A stock Faction Crier action accepted a changed currency.");
        }
        catch (PostgresException exception) when (
            exception.SqlState == PostgresErrorCodes.CheckViolation &&
            exception.ConstraintName ==
                "ck_faction_crier_balance_option_stock_shape")
        {
            // Stock client text and result sub-IDs require this exact shape.
        }
    }

    private static async Task AssertRewardOverflowPublicationGuardAsync(
        NpgsqlDataSource dataSource,
        long activeRevision)
    {
        var candidateRevision = checked(activeRevision + 1);
        await using (var connection = await dataSource.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await InsertCandidateHeaderAsync(
                connection,
                transaction,
                activeRevision,
                candidateRevision);
            await using (var children = new NpgsqlCommand(
                """
                INSERT INTO public.faction_crier_balance_tiers
                SELECT @candidateRevision, minimum_level, maximum_level,
                       CASE WHEN minimum_level = 20
                            THEN 2147483647
                            ELSE base_experience END,
                       base_talent_points,
                       triple_silver_cost, all_six_silver_cost
                FROM public.faction_crier_balance_tiers
                WHERE balance_revision = @activeRevision;

                INSERT INTO public.faction_crier_balance_options
                SELECT @candidateRevision, sub_id, currency_code, cost,
                       multiplier, reward_kind
                FROM public.faction_crier_balance_options
                WHERE balance_revision = @activeRevision;
                """,
                connection,
                transaction))
            {
                AddGuardRevisions(
                    children,
                    activeRevision,
                    candidateRevision);
                Check.Equal(
                    32,
                    await children.ExecuteNonQueryAsync(),
                    "overflow candidate owns a complete child set");
            }

            try
            {
                await using var publish = new NpgsqlCommand(
                    """
                    UPDATE public.faction_crier_balance_settings
                    SET revision = @candidateRevision,
                        updated_by = 'overflow-protocol-check'
                    WHERE setting_id = 1
                      AND revision = @activeRevision;
                    """,
                    connection,
                    transaction);
                AddGuardRevisions(
                    publish,
                    activeRevision,
                    candidateRevision);
                await publish.ExecuteNonQueryAsync();
                throw new InvalidOperationException(
                    "An overflowing Faction Crier reward was published.");
            }
            catch (PostgresException exception) when (
                exception.SqlState == PostgresErrorCodes.RaiseException &&
                exception.MessageText.Contains(
                    "incomplete or unsafe",
                    StringComparison.Ordinal))
            {
                // Publication must fail before workers can pin unsafe arithmetic.
            }
        }

        Check.Equal(
            activeRevision,
            await ReadActiveRevisionAsync(dataSource),
            "rejected overflow leaves the active balance unchanged");
    }

    private static async Task InsertCandidateHeaderAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long activeRevision,
        long candidateRevision)
    {
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO public.faction_crier_balance_revisions (
                revision, server_utc_offset_minutes, minimum_level,
                weekly_reclaim_gold_cost, renewal_gold_cost,
                tier_count, option_count, created_by)
            SELECT @candidateRevision, server_utc_offset_minutes,
                   minimum_level, weekly_reclaim_gold_cost,
                   renewal_gold_cost, tier_count, option_count,
                   'guard-protocol-check'
            FROM public.faction_crier_balance_revisions
            WHERE revision = @activeRevision;
            """,
            connection,
            transaction);
        AddGuardRevisions(command, activeRevision, candidateRevision);
        Check.Equal(
            1,
            await command.ExecuteNonQueryAsync(),
            "management guard creates one candidate header");
    }

    private static async Task<long> ReadActiveRevisionAsync(
        NpgsqlDataSource dataSource)
    {
        await using var command = dataSource.CreateCommand(
            """
            SELECT revision
            FROM public.faction_crier_balance_settings
            WHERE setting_id = 1;
            """);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static void AddGuardRevisions(
        NpgsqlCommand command,
        long activeRevision,
        long candidateRevision)
    {
        command.Parameters.AddWithValue("activeRevision", activeRevision);
        command.Parameters.AddWithValue(
            "candidateRevision",
            candidateRevision);
    }
}
