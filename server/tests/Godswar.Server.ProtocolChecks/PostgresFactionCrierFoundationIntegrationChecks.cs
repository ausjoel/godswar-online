using System.Text.RegularExpressions;
using Godswar.Server.Application.FactionCrier;
using Godswar.Server.Infrastructure.Database;
using Godswar.Server.Infrastructure.FactionCrier;
using Godswar.Server.Infrastructure.Items;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresFactionCrierFoundationIntegrationChecks
{
    public const string CheckName =
        "PostgreSQL Faction Crier foundation";

    private const string ConnectionStringVariable =
        "GODSWAR_TEST_POSTGRES_CONNECTION_STRING";

    // Historical lineage entry counts, captured before the reviewed
    // client-catalog family existed.
    //   fresh publication   : 1758 (no elemental tombstones) + the reviewed
    //                         client-catalog identity count
    //   live upgrade lineage: the complete reviewed catalog, i.e. the
    //                         1773-identity capture-tool release plus the
    //                         reviewed client catalog
    // The lineage-specific revision SHAs are content-derived and are re-pinned
    // by the controlled-host run.
    private const int ReviewedClientCatalogEntryCount =
        ClientCatalogItemContentBaseline.ShippedItemCount;
    private const int FreshCaptureToolEntryCount =
        1758 + ReviewedClientCatalogEntryCount;
    private const int LiveUpgradeCaptureToolEntryCount =
        1773 + ReviewedClientCatalogEntryCount;

    private static readonly Regex DisposableDatabasePattern = new(
        @"^godswar_(?:b09|b12)_[a-z0-9_]{1,48}$",
        RegexOptions.CultureInvariant);

    public static async Task RunAsync()
    {
        var connectionString =
            Environment.GetEnvironmentVariable(ConnectionStringVariable);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new CheckSkippedException($"{CheckName} ({ConnectionStringVariable} is not set)");
        }

        await using var dataSource = NpgsqlDataSource.Create(
            connectionString);
        var database = await ReadDatabaseNameAsync(dataSource);
        if (!DisposableDatabasePattern.IsMatch(database))
        {
            throw new CheckSkippedException($"{CheckName} requires a disposable B09/B12 database; " +
                $"received '{database}'");
        }

        await PostgresSchemaStartup.InitializeAsync(connectionString);
        var pinned = await PostgresFactionCrierBalanceSnapshotReader
            .LoadAsync(dataSource);
        Check.True(
            pinned.Revision == 1 &&
            pinned.MinimumLevel == 20 &&
            pinned.WeeklyReclaimGoldCost == 230 &&
            pinned.RenewalGoldCost == 105 &&
            pinned.Tiers.Count == 7 &&
            pinned.Tiers[^1].MaximumLevel == 200 &&
            pinned.Options.Count == 25,
            "startup pins the reviewed revisioned Faction Crier balance");

        var publication = await PostgresItemTemplateBaselinePublisher
            .EnsurePublishedAsync(dataSource);
        Check.True(
            publication.EntryCount == FreshCaptureToolEntryCount ||
            publication.EntryCount == LiveUpgradeCaptureToolEntryCount,
            "immutable item content includes Nameplates, the Storage Box Key, the capture tool, and the reviewed client catalog");
        await AssertNameplatePublicationAsync(
            dataSource,
            publication.Revision,
            publication.EntryCount);
        await AssertBindingGoldStorageAsync(dataSource);
        await AssertManagementCompareExchangeAsync(dataSource, pinned);
        await AssertManagementSafetyGuardsAsync(dataSource);
    }

    private static async Task AssertNameplatePublicationAsync(
        NpgsqlDataSource dataSource,
        string revision,
        int entryCount)
    {
        await using var command = dataSource.CreateCommand(
            """
            SELECT header.source,
                   header.entry_count,
                   count(definition.id)::integer,
                   min(definition.id),
                   max(definition.id),
                   bool_and(
                       definition.stats ->> 'Overlap' = '99'
                       AND definition.stats ->> 'BindType' = '1'
                       AND definition.kind = 'consume item'
                   )
            FROM public.item_template_content_revisions header
            JOIN public.item_template_content_definitions definition
              ON definition.revision = header.revision
             AND definition.id BETWEEN 3820 AND 3825
            WHERE header.revision = @revision
            GROUP BY header.source, header.entry_count;
            """);
        command.Parameters.AddWithValue("revision", revision);
        await using var reader = await command.ExecuteReaderAsync();
        Check.True(
            await reader.ReadAsync() &&
            reader.GetString(0).EndsWith(
                "+pets-v5+nameplates-v1+warehouse-v1+opal-v1+" +
                "client-catalog-v1",
                StringComparison.Ordinal) &&
            reader.GetInt32(1) == entryCount &&
            reader.GetInt32(2) == 6 &&
            reader.GetInt32(3) == 3820 &&
            reader.GetInt32(4) == 3825 &&
            reader.GetBoolean(5) &&
            !await reader.ReadAsync(),
            "published Nameplates retain exact stack, bind, and lineage data");
    }

    private static async Task AssertBindingGoldStorageAsync(
        NpgsqlDataSource dataSource)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        int accountId;
        await using (var account = new NpgsqlCommand(
            """
            INSERT INTO public.accounts (username, password)
            VALUES (@username, '')
            RETURNING id;
            """,
            connection,
            transaction))
        {
            account.Parameters.AddWithValue(
                "username",
                $"fc_wallet_{Guid.NewGuid():N}"[..32]);
            accountId = Convert.ToInt32(await account.ExecuteScalarAsync());
        }

        int realmId;
        await using (var realm = new NpgsqlCommand(
            "SELECT id FROM public.server ORDER BY id LIMIT 1;",
            connection,
            transaction))
        {
            realmId = Convert.ToInt32(await realm.ExecuteScalarAsync());
        }

        int characterId;
        await using (var character = new NpgsqlCommand(
            """
            INSERT INTO public.character_base (
                account_id, server_id, name, camp, profession,
                fighter_job_lv, "Money", "Stone", "BindingGold")
            VALUES (
                @accountId, @realmId, @name, 1, 0, 80, 123, 456, 789)
            RETURNING id;
            """,
            connection,
            transaction))
        {
            character.Parameters.AddWithValue("accountId", accountId);
            character.Parameters.AddWithValue("realmId", realmId);
            character.Parameters.AddWithValue(
                "name",
                $"FcWallet{Guid.NewGuid():N}"[..32]);
            characterId = Convert.ToInt32(
                await character.ExecuteScalarAsync());
        }

        await using (var baseline = new NpgsqlCommand(
            """
            INSERT INTO public.character_economy_baseline (
                character_id, account_id, silver, gold, binding_gold,
                item_count, baseline_source)
            VALUES (@characterId, @accountId, 123, 456, 789, 0,
                    'faction-crier-check');
            """,
            connection,
            transaction))
        {
            baseline.Parameters.AddWithValue("characterId", characterId);
            baseline.Parameters.AddWithValue("accountId", accountId);
            Check.Equal(
                1,
                await baseline.ExecuteNonQueryAsync(),
                "B-Gold economy baseline is durable");
        }

        await using (var reconciliation = new NpgsqlCommand(
            """
            SELECT baseline_binding_gold,
                   current_binding_gold,
                   expected_binding_gold,
                   balances_match,
                   is_reconciled
            FROM public.character_wallet_reconciliation
            WHERE character_id = @characterId;
            """,
            connection,
            transaction))
        {
            reconciliation.Parameters.AddWithValue(
                "characterId",
                characterId);
            await using var reader =
                await reconciliation.ExecuteReaderAsync();
            Check.True(
                await reader.ReadAsync() &&
                reader.GetInt64(0) == 789 &&
                reader.GetInt64(1) == 789 &&
                reader.GetInt64(2) == 789 &&
                reader.GetBoolean(3) &&
                reader.GetBoolean(4) &&
                !await reader.ReadAsync(),
                "B-Gold participates in wallet reconciliation");
        }
        await transaction.RollbackAsync();
    }

    private static async Task AssertManagementCompareExchangeAsync(
        NpgsqlDataSource dataSource,
        FactionCrierBalanceSnapshot pinned)
    {
        var nextRevision = checked(pinned.Revision + 1);
        await using (var connection = await dataSource.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await using (var header = new NpgsqlCommand(
                """
                INSERT INTO public.faction_crier_balance_revisions (
                    revision, server_utc_offset_minutes, minimum_level,
                    weekly_reclaim_gold_cost, renewal_gold_cost,
                    tier_count, option_count, created_by)
                SELECT @nextRevision, server_utc_offset_minutes,
                       minimum_level, weekly_reclaim_gold_cost,
                       renewal_gold_cost, tier_count, option_count,
                       'protocol-check'
                FROM public.faction_crier_balance_revisions
                WHERE revision = @expectedRevision;
                """,
                connection,
                transaction))
            {
                AddRevisions(header, pinned.Revision, nextRevision);
                Check.Equal(1, await header.ExecuteNonQueryAsync(),
                    "management creates one successor header");
            }

            await CopyBalanceChildrenAsync(
                connection,
                transaction,
                pinned.Revision,
                nextRevision);
            await using var publish = new NpgsqlCommand(
                """
                UPDATE public.faction_crier_balance_settings
                SET revision = @nextRevision,
                    updated_by = 'protocol-check'
                WHERE setting_id = 1
                  AND revision = @expectedRevision;
                """,
                connection,
                transaction);
            AddRevisions(publish, pinned.Revision, nextRevision);
            Check.Equal(1, await publish.ExecuteNonQueryAsync(),
                "matching management revision publishes atomically");
            await transaction.CommitAsync();
        }

        await using (var stale = dataSource.CreateCommand(
            """
            UPDATE public.faction_crier_balance_settings
            SET revision = @nextRevision,
                updated_by = 'stale-protocol-check'
            WHERE setting_id = 1
              AND revision = @expectedRevision;
            """))
        {
            AddRevisions(stale, pinned.Revision, nextRevision);
            Check.Equal(0, await stale.ExecuteNonQueryAsync(),
                "stale management publication does not mutate authority");
        }

        var active = await PostgresFactionCrierBalanceSnapshotReader
            .LoadAsync(dataSource);
        Check.True(
            active.Revision == nextRevision &&
            pinned.Revision + 1 == active.Revision,
            "new workers see the published revision while the old snapshot " +
            "remains pinned");

        try
        {
            await using var insert = dataSource.CreateCommand(
                """
                INSERT INTO public.faction_crier_balance_tiers (
                    balance_revision, minimum_level, maximum_level,
                    base_experience, base_talent_points,
                    triple_silver_cost, all_six_silver_cost)
                VALUES (@revision, 199, 199, 1, 1, 0, 0);
                """);
            insert.Parameters.AddWithValue("revision", nextRevision);
            await insert.ExecuteNonQueryAsync();
            throw new InvalidOperationException(
                "A sealed balance accepted a child insert.");
        }
        catch (PostgresException exception)
            when (exception.SqlState == "55000")
        {
            // The sealed publication is append-closed by design.
        }
    }

    private static async Task CopyBalanceChildrenAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long expectedRevision,
        long nextRevision)
    {
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO public.faction_crier_balance_tiers
            SELECT @nextRevision, minimum_level, maximum_level,
                   base_experience, base_talent_points,
                   triple_silver_cost, all_six_silver_cost
            FROM public.faction_crier_balance_tiers
            WHERE balance_revision = @expectedRevision;

            INSERT INTO public.faction_crier_balance_options
            SELECT @nextRevision, sub_id, currency_code, cost,
                   multiplier, reward_kind
            FROM public.faction_crier_balance_options
            WHERE balance_revision = @expectedRevision;
            """,
            connection,
            transaction);
        AddRevisions(command, expectedRevision, nextRevision);
        Check.Equal(32, await command.ExecuteNonQueryAsync(),
            "management copies seven tiers and twenty-five options");
    }

    private static void AddRevisions(
        NpgsqlCommand command,
        long expectedRevision,
        long nextRevision)
    {
        command.Parameters.AddWithValue("expectedRevision", expectedRevision);
        command.Parameters.AddWithValue("nextRevision", nextRevision);
    }

    private static async Task<string> ReadDatabaseNameAsync(
        NpgsqlDataSource dataSource)
    {
        await using var command = dataSource.CreateCommand(
            "SELECT current_database();");
        return Convert.ToString(await command.ExecuteScalarAsync()) ??
            string.Empty;
    }
}
