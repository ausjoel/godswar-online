using System.Text.RegularExpressions;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresRealmMigrationIntegrationChecks
{
    private static async Task<int> InsertAccountAsync(
        NpgsqlDataSource dataSource,
        string username)
    {
        await using var command = dataSource.CreateCommand(
            """
            INSERT INTO public.accounts (username, password)
            VALUES (@username, '')
            RETURNING id;
            """);
        command.Parameters.AddWithValue("username", username);
        return Convert.ToInt32(
            await command.ExecuteScalarAsync());
    }

    private static async Task<int> InsertCharacterAsync(
        NpgsqlDataSource dataSource,
        int accountId,
        string name,
        string realmExpression)
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
            )
            RETURNING id;
            """);
        command.Parameters.AddWithValue("accountId", accountId);
        command.Parameters.AddWithValue("name", name);
        return Convert.ToInt32(
            await command.ExecuteScalarAsync());
    }

    private static async Task<int> ReadCharacterRealmAsync(
        NpgsqlDataSource dataSource,
        int characterId)
    {
        await using var command = dataSource.CreateCommand(
            """
            SELECT server_id
            FROM public.character_base
            WHERE id = @characterId;
            """);
        command.Parameters.AddWithValue(
            "characterId",
            characterId);
        return Convert.ToInt32(
            await command.ExecuteScalarAsync());
    }

    private static async Task<bool> IsCharacterRealmNullAsync(
        NpgsqlDataSource dataSource,
        int characterId)
    {
        await using var command = dataSource.CreateCommand(
            """
            SELECT server_id IS NULL
            FROM public.character_base
            WHERE id = @characterId;
            """);
        command.Parameters.AddWithValue(
            "characterId",
            characterId);
        return Convert.ToBoolean(
            await command.ExecuteScalarAsync());
    }

    private static async Task<bool> IsRealmColumnNullableAsync(
        NpgsqlDataSource dataSource) =>
        string.Equals(
            await ReadTextAsync(
                dataSource,
                """
                SELECT is_nullable
                FROM information_schema.columns
                WHERE table_schema = 'public'
                  AND table_name = 'character_base'
                  AND column_name = 'server_id';
                """),
            "YES",
            StringComparison.Ordinal);

    private static Task<string> ReadRealmColumnDefaultAsync(
        NpgsqlDataSource dataSource) =>
        ReadTextAsync(
            dataSource,
            """
            SELECT column_default
            FROM information_schema.columns
            WHERE table_schema = 'public'
              AND table_name = 'character_base'
              AND column_name = 'server_id';
            """);

    private static async Task<bool> IsMigrationAppliedAsync(
        NpgsqlDataSource dataSource)
    {
        await using var command = dataSource.CreateCommand(
            """
            SELECT EXISTS (
                SELECT 1
                FROM public.schema_migrations
                WHERE migration_id = @migrationId
            );
            """);
        command.Parameters.AddWithValue(
            "migrationId",
            MigrationId);
        return Convert.ToBoolean(
            await command.ExecuteScalarAsync());
    }

    private static Task<int> ReadMigrationCountAsync(
        NpgsqlDataSource dataSource) =>
        ReadInt32Async(
            dataSource,
            """
            SELECT count(*)::integer
            FROM public.schema_migrations;
            """);

    private static async Task<int> ReadInt32Async(
        NpgsqlDataSource dataSource,
        string sql) =>
        Convert.ToInt32(
            await ExecuteScalarAsync(dataSource, sql));

    private static async Task<string> ReadTextAsync(
        NpgsqlDataSource dataSource,
        string sql) =>
        Convert.ToString(
            await ExecuteScalarAsync(dataSource, sql))
        ?? throw new InvalidDataException(
            "PostgreSQL returned no text value.");

    private static async Task<object> ExecuteScalarAsync(
        NpgsqlDataSource dataSource,
        string sql)
    {
        await using var command = dataSource.CreateCommand(sql);
        return await command.ExecuteScalarAsync()
            ?? throw new InvalidDataException(
                "PostgreSQL returned no scalar value.");
    }

    private static async Task ExecuteAsync(
        NpgsqlDataSource dataSource,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        await using var command = dataSource.CreateCommand(sql);
        foreach (var parameter in parameters)
        {
            command.Parameters.AddWithValue(
                parameter.Name,
                parameter.Value);
        }

        _ = await command.ExecuteNonQueryAsync();
    }

    private static Task DeleteAccountAsync(
        NpgsqlDataSource dataSource,
        int accountId) =>
        ExecuteAsync(
            dataSource,
            """
            DELETE FROM public.accounts
            WHERE id = @accountId;
            """,
            ("accountId", accountId));

    private static async Task DeleteFixtureAsync(
        NpgsqlDataSource dataSource,
        RealmFixture fixture)
    {
        await DeleteAccountAsync(
            dataSource,
            fixture.AccountId);
        await ExecuteAsync(
            dataSource,
            """
            DELETE FROM public.server
            WHERE id = 2
              AND identifier = 'b18a-future-realm';
            """);
    }

    private sealed record RealmFixture(
        int AccountId,
        int CharacterId,
        string Token);
}
