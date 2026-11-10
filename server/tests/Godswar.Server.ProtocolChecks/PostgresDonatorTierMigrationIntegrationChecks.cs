using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static class PostgresDonatorTierMigrationIntegrationChecks
{
    internal const string CheckName =
        "PostgreSQL Donator tier forward migration";

    private const string ConnectionStringVariable =
        "GODSWAR_TEST_POSTGRES_CONNECTION_STRING";
    private const string MigrationId =
        "20260831_128_donator_tiers";

    public static async Task RunAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable(
            ConnectionStringVariable);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new CheckSkippedException($"{CheckName} " +
                $"({ConnectionStringVariable} is not set)");
        }

        await using var dataSource = NpgsqlDataSource.Create(
            connectionString);
        var runner = new PostgresSchemaMigrationRunner(dataSource);
        await runner.InitializeAsync(
            LegacySchemaBootstrap.LoadAsync,
            PostgresSchemaMigrationCatalog.All);

        var migration = PostgresSchemaMigrationCatalog.All.Single(
            entry => entry.Id == MigrationId);
        await AssertForwardMigrationAsync(dataSource, migration);
    }

    private static async Task AssertForwardMigrationAsync(
        NpgsqlDataSource dataSource,
        PostgresSchemaMigration migration)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        try
        {
            await ReverseToLegacyShapeAsync(connection, transaction);
            var token = Guid.NewGuid().ToString("N")[..12];
            var legacyUsername = $"donator_legacy_{token}";
            var newUsername = $"donator_new_{token}";
            var legacyExpiresAt = new DateTimeOffset(
                2027,
                1,
                15,
                0,
                0,
                0,
                TimeSpan.Zero);
            await InsertLegacyTierFourAsync(
                connection,
                transaction,
                legacyUsername,
                legacyExpiresAt);

            await using (var migrate = new NpgsqlCommand(
                             migration.Sql,
                             connection,
                             transaction))
            {
                await migrate.ExecuteNonQueryAsync();
            }

            var migratedLegacy = await ReadEntitlementAsync(
                connection,
                transaction,
                legacyUsername);
            Check.Equal(
                (short)4,
                migratedLegacy.Tier,
                "migration 128 preserves a legacy tier-four account");
            Check.Equal(
                legacyExpiresAt,
                migratedLegacy.ExpiresAt!.Value,
                "migration 128 preserves the legacy membership expiry");
            await InsertTierFiveAsync(
                connection,
                transaction,
                newUsername);
            Check.Equal(
                (short)5,
                (await ReadEntitlementAsync(
                    connection,
                    transaction,
                    newUsername)).Tier,
                "migration 128 accepts the new Octagram tier five");
            var constraints = await ReadConstraintStateAsync(
                connection,
                transaction);
            Check.Equal(
                0,
                constraints.LegacyCount,
                "migration 128 removes both arbitrary legacy CHECK names");
            Check.True(
                constraints.CurrentCount == 1 &&
                constraints.CurrentValidated,
                "migration 128 leaves exactly one validated Donator tier " +
                "constraint");
        }
        finally
        {
            await transaction.RollbackAsync();
        }
    }

    private static async Task ReverseToLegacyShapeAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction)
    {
        await using var command = new NpgsqlCommand(
            """
            ALTER TABLE public.accounts
                DROP CONSTRAINT ck_accounts_donator_tier;

            ALTER TABLE public.accounts
                RENAME COLUMN donator_tier TO vip_tier;

            ALTER TABLE public.accounts
                RENAME COLUMN donator_expires_at TO vip_expires_at;

            ALTER TABLE public.accounts
                ADD CONSTRAINT "legacy vip tier four check"
                    CHECK (vip_tier BETWEEN 0 AND 4),
                ADD CONSTRAINT "legacy ""quoted"" vip tier guard"
                    CHECK (vip_tier <> 99);
            """,
            connection,
            transaction);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task InsertLegacyTierFourAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string username,
        DateTimeOffset expiresAt)
    {
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO public.accounts (
                username,
                password,
                vip_tier,
                vip_expires_at)
            VALUES (@username, '', 4, @expiresAt);
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("username", username);
        command.Parameters.AddWithValue("expiresAt", expiresAt);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task InsertTierFiveAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string username)
    {
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO public.accounts (username, password, donator_tier)
            VALUES (@username, '', 5);
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("username", username);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<EntitlementState> ReadEntitlementAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string username)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT donator_tier, donator_expires_at
            FROM public.accounts
            WHERE username = @username;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("username", username);
        await using var reader = await command.ExecuteReaderAsync();
        Check.True(
            await reader.ReadAsync(),
            "migration fixture account remains readable");
        return new EntitlementState(
            reader.GetInt16(0),
            reader.IsDBNull(1)
                ? null
                : reader.GetFieldValue<DateTimeOffset>(1));
    }

    private static async Task<ConstraintState> ReadConstraintStateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT
                count(*) FILTER (
                    WHERE candidate.conname IN (
                        'legacy vip tier four check',
                        'legacy "quoted" vip tier guard'))::integer,
                count(*) FILTER (
                    WHERE candidate.conname =
                        'ck_accounts_donator_tier')::integer,
                coalesce(bool_and(candidate.convalidated) FILTER (
                    WHERE candidate.conname =
                        'ck_accounts_donator_tier'), false)
            FROM pg_catalog.pg_constraint AS candidate
            JOIN pg_catalog.pg_class AS relation
              ON relation.oid = candidate.conrelid
            JOIN pg_catalog.pg_namespace AS schema_namespace
              ON schema_namespace.oid = relation.relnamespace
            WHERE schema_namespace.nspname = 'public'
              AND relation.relname = 'accounts'
              AND candidate.contype = 'c';
            """,
            connection,
            transaction);
        await using var reader = await command.ExecuteReaderAsync();
        Check.True(
            await reader.ReadAsync(),
            "migration constraint state is readable");
        return new ConstraintState(
            reader.GetInt32(0),
            reader.GetInt32(1),
            reader.GetBoolean(2));
    }

    private sealed record EntitlementState(
        short Tier,
        DateTimeOffset? ExpiresAt);

    private sealed record ConstraintState(
        int LegacyCount,
        int CurrentCount,
        bool CurrentValidated);
}
