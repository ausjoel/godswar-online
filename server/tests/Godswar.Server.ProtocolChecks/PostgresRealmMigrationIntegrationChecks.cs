using System.Text.RegularExpressions;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresRealmMigrationIntegrationChecks
{
    public const string CheckName =
        "PostgreSQL Tempest realm authority migration";

    private const string ConnectionStringVariable =
        "GODSWAR_TEST_POSTGRES_CONNECTION_STRING";

    private const string MigrationId =
        "20260731_035_tempest_realm_authority";

    private static readonly Regex DisposableDatabasePattern = new(
        @"^godswar_(?:b03_[a-f0-9]{10}_realm_authority|b18a_[a-z0-9_]{1,40})$",
        RegexOptions.CultureInvariant);

    public static async Task RunAsync()
    {
        var connectionString =
            Environment.GetEnvironmentVariable(
                ConnectionStringVariable);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new CheckSkippedException($"{CheckName} " +
                $"({ConnectionStringVariable} is not set)");
        }

        await using var dataSource =
            NpgsqlDataSource.Create(connectionString);
        var databaseName = await ReadTextAsync(
            dataSource,
            "SELECT current_database();");
        if (!IsDisposableDatabaseName(databaseName))
        {
            throw new InvalidOperationException(
                "Realm migration checks require a bounded disposable " +
                $"database; received '{databaseName}'.");
        }

        var runner = new PostgresSchemaMigrationRunner(dataSource);
        await runner.InitializeAsync(
            LegacySchemaBootstrap.LoadAsync,
            PostgresSchemaMigrationCatalog.All
                .Take(35)
                .ToArray());
        var fixture = await CreateLegacyFixtureAsync(dataSource);
        try
        {
            await AssertIdentityConflictRollsBackAsync(
                runner,
                dataSource,
                fixture);
            await AssertNonTempestConflictRollsBackAsync(
                runner,
                dataSource,
                fixture);
            await runner.InitializeAsync(
                LegacySchemaBootstrap.LoadAsync,
                MigrationsThrough(MigrationId));
            await AssertAppliedRealmAuthorityAsync(
                dataSource,
                fixture);

            await runner.InitializeAsync(
                LegacySchemaBootstrap.LoadAsync,
                PostgresSchemaMigrationCatalog.All);
            await AssertAppliedMultiRealmAuthorityAsync(
                dataSource,
                fixture);

            var beforeRepeat = await ReadMigrationCountAsync(dataSource);
            await runner.InitializeAsync(
                LegacySchemaBootstrap.LoadAsync,
                PostgresSchemaMigrationCatalog.All);
            Check.Equal(
                beforeRepeat,
                await ReadMigrationCountAsync(dataSource),
                "repeated realm initialization is a migration no-op");
        }
        finally
        {
            await DeleteFixtureAsync(dataSource, fixture);
        }
    }

    internal static bool IsDisposableDatabaseName(string databaseName) =>
        DisposableDatabasePattern.IsMatch(databaseName);

    private static IReadOnlyList<PostgresSchemaMigration> MigrationsThrough(
        string migrationId)
    {
        var migrations = PostgresSchemaMigrationCatalog.All;
        var finalIndex = migrations
            .Select(static (migration, index) => (migration, index))
            .Single(candidate => candidate.migration.Id == migrationId)
            .index;
        return migrations.Take(finalIndex + 1).ToArray();
    }

    private static async Task<RealmFixture> CreateLegacyFixtureAsync(
        NpgsqlDataSource dataSource)
    {
        var token = Guid.NewGuid().ToString("N")[..12];
        await using var command = dataSource.CreateCommand(
            """
            WITH account_row AS (
                INSERT INTO public.accounts (username, password)
                VALUES (@username, '')
                RETURNING id
            )
            INSERT INTO public.character_base (
                account_id,
                server_id,
                name
            )
            SELECT
                account_row.id,
                NULL,
                @characterName
            FROM account_row
            RETURNING account_id, id;
            """);
        command.Parameters.AddWithValue(
            "username",
            $"b18a_realm_{token}");
        command.Parameters.AddWithValue(
            "characterName",
            $"B18A{token}");
        await using var reader = await command.ExecuteReaderAsync();
        Check.True(
            await reader.ReadAsync(),
            "legacy fixture creates one unassigned character");
        return new RealmFixture(
            reader.GetInt32(0),
            reader.GetInt32(1),
            token);
    }

    private static async Task AssertIdentityConflictRollsBackAsync(
        PostgresSchemaMigrationRunner runner,
        NpgsqlDataSource dataSource,
        RealmFixture fixture)
    {
        await ExecuteAsync(
            dataSource,
            """
            UPDATE public.server
            SET name = 'Not Tempest'
            WHERE id = 1;
            """);
        await AssertMigrationRejectedAsync(
            runner,
            dataSource,
            PostgresErrorCodes.CheckViolation,
            "wrong realm-one identity fails closed");
        Check.True(
            await IsCharacterRealmNullAsync(
                dataSource,
                fixture.CharacterId),
            "failed identity preflight rolls back the character backfill");
        Check.True(
            !await IsMigrationAppliedAsync(dataSource),
            "failed identity preflight records no migration history");
        Check.True(
            await IsRealmColumnNullableAsync(dataSource),
            "failed identity preflight rolls back column hardening");
        await ExecuteAsync(
            dataSource,
            """
            UPDATE public.server
            SET name = 'Tempest'
            WHERE id = 1;
            """);
    }

    private static async Task AssertNonTempestConflictRollsBackAsync(
        PostgresSchemaMigrationRunner runner,
        NpgsqlDataSource dataSource,
        RealmFixture fixture)
    {
        await ExecuteAsync(
            dataSource,
            """
            INSERT INTO public.server (
                id,
                name,
                identifier,
                ip_address,
                server_limit
            )
            VALUES (
                2,
                'Future Realm',
                'b18a-future-realm',
                '127.0.0.2',
                1
            );
            """);
        await ExecuteAsync(
            dataSource,
            """
            UPDATE public.character_base
            SET server_id = 2
            WHERE id = @characterId;
            """,
            ("characterId", fixture.CharacterId));
        await AssertMigrationRejectedAsync(
            runner,
            dataSource,
            PostgresErrorCodes.CheckViolation,
            "premature non-Tempest character fails closed");
        Check.Equal(
            2,
            await ReadCharacterRealmAsync(
                dataSource,
                fixture.CharacterId),
            "failed non-Tempest preflight preserves the source row");
        Check.True(
            !await IsMigrationAppliedAsync(dataSource),
            "non-Tempest rejection records no migration history");
        await ExecuteAsync(
            dataSource,
            """
            UPDATE public.character_base
            SET server_id = NULL
            WHERE id = @characterId;

            DELETE FROM public.server
            WHERE id = 2;
            """,
            ("characterId", fixture.CharacterId));
    }

    private static async Task AssertAppliedRealmAuthorityAsync(
        NpgsqlDataSource dataSource,
        RealmFixture fixture)
    {
        Check.True(
            await IsMigrationAppliedAsync(dataSource),
            "remediated database applies Tempest realm authority");
        Check.Equal(
            1,
            await ReadCharacterRealmAsync(
                dataSource,
                fixture.CharacterId),
            "legacy unassigned character backfills to Tempest");
        Check.True(
            !await IsRealmColumnNullableAsync(dataSource),
            "character realm becomes required");
        Check.Equal(
            "1",
            await ReadRealmColumnDefaultAsync(dataSource),
            "new character rows default to Tempest");
        Check.Equal(
            1,
            await ReadInt32Async(
                dataSource,
                """
                SELECT count(*)::integer
                FROM public.server
                WHERE id = 1
                  AND name = 'Tempest'
                  AND identifier = 'KAL3jcIzqGgKvOf1dbYZKC8cS';
                """),
            "Tempest retains its exact durable identity");
        Check.Equal(
            1,
            await ReadInt32Async(
                dataSource,
                """
                SELECT count(*)::integer
                FROM pg_constraint
                WHERE conrelid =
                        'public.character_base'::regclass
                  AND conname =
                        'ck_character_base_tempest_realm'
                  AND contype = 'c'
                  AND convalidated;
                """),
            "single-realm character constraint is validated");
        Check.Equal(
            1,
            await ReadInt32Async(
                dataSource,
                """
                SELECT count(*)::integer
                FROM pg_constraint constraint_row
                WHERE constraint_row.conrelid =
                        'public.character_base'::regclass
                  AND constraint_row.confrelid =
                        'public.server'::regclass
                  AND constraint_row.contype = 'f'
                  AND constraint_row.convalidated;
                """),
            "character realm foreign key remains validated");
        Check.Equal(
            1,
            await ReadInt32Async(
                dataSource,
                """
                SELECT count(*)::integer
                FROM pg_index index_row
                JOIN pg_class index_class
                  ON index_class.oid = index_row.indexrelid
                WHERE index_row.indrelid =
                        'public.character_base'::regclass
                  AND index_class.relname =
                        'ix_character_base_server'
                  AND index_row.indisvalid
                  AND index_row.indisready;
                """),
            "character realm lookup index remains valid");

        var defaultAccountId = await InsertAccountAsync(
            dataSource,
            $"b18a_default_{fixture.Token}");
        try
        {
            var defaultCharacterId = await InsertCharacterAsync(
                dataSource,
                defaultAccountId,
                $"B18AD{fixture.Token}",
                "DEFAULT");
            Check.Equal(
                1,
                await ReadCharacterRealmAsync(
                    dataSource,
                    defaultCharacterId),
                "omitted realm uses Tempest default");

            await AssertInsertRejectedAsync(
                dataSource,
                defaultAccountId,
                $"B18AN{fixture.Token}",
                "NULL",
                PostgresErrorCodes.NotNullViolation,
                "explicit null realm is rejected");
        }
        finally
        {
            await DeleteAccountAsync(dataSource, defaultAccountId);
        }
    }

    private static async Task AssertMigrationRejectedAsync(
        PostgresSchemaMigrationRunner runner,
        NpgsqlDataSource dataSource,
        string expectedSqlState,
        string description)
    {
        try
        {
            await runner.InitializeAsync(
                LegacySchemaBootstrap.LoadAsync,
                PostgresSchemaMigrationCatalog.All);
            throw new InvalidOperationException(
                $"Expected migration rejection: {description}.");
        }
        catch (PostgresException exception)
            when (exception.SqlState == expectedSqlState)
        {
        }
    }

    private static async Task AssertInsertRejectedAsync(
        NpgsqlDataSource dataSource,
        int accountId,
        string name,
        string realmExpression,
        string expectedSqlState,
        string description)
    {
        await using var command = dataSource.CreateCommand($"""
            INSERT INTO public.character_base (
                account_id,
                server_id,
                name
            )
            VALUES (
                @accountId,
                {realmExpression},
                @name
            );
            """);
        command.Parameters.AddWithValue("accountId", accountId);
        command.Parameters.AddWithValue("name", name);
        try
        {
            _ = await command.ExecuteNonQueryAsync();
            throw new InvalidOperationException(
                $"Expected insert rejection: {description}.");
        }
        catch (PostgresException exception)
            when (exception.SqlState == expectedSqlState)
        {
        }
    }
}
