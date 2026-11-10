using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresPetInitialSavvyMigrationIntegrationChecks
{
    private static async Task ExecuteAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        await using var command =
            new NpgsqlCommand(sql, connection, transaction);
        foreach (var parameter in parameters)
        {
            command.Parameters.AddWithValue(
                parameter.Name,
                parameter.Value);
        }

        await command.ExecuteNonQueryAsync();
    }

    private static async Task<bool> IsMigrationAppliedAsync(
        string connectionString,
        string migrationId)
    {
        if (!await RelationExistsAsync(
                connectionString,
                "public.schema_migrations"))
        {
            return false;
        }

        await using var connection =
            new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT EXISTS (
                SELECT 1
                FROM public.schema_migrations
                WHERE migration_id = @migrationId
            );
            """,
            connection);
        command.Parameters.AddWithValue("migrationId", migrationId);
        return (bool)(await command.ExecuteScalarAsync()
                      ?? throw new InvalidOperationException(
                          "Migration-presence check returned null."));
    }

    private static async Task<bool> RelationExistsAsync(
        string connectionString,
        string qualifiedName)
    {
        await using var connection =
            new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT to_regclass(@qualifiedName) IS NOT NULL;",
            connection);
        command.Parameters.AddWithValue("qualifiedName", qualifiedName);
        return (bool)(await command.ExecuteScalarAsync()
                      ?? throw new InvalidOperationException(
                          "Relation-presence check returned null."));
    }

    private static async Task<long> CountFixtureAccountsAsync(
        string connectionString,
        string username)
    {
        await using var connection =
            new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT count(*)
            FROM public.accounts
            WHERE username = @username;
            """,
            connection);
        command.Parameters.AddWithValue("username", username);
        return (long)(await command.ExecuteScalarAsync()
                      ?? throw new InvalidOperationException(
                          "Fixture-cleanup check returned null."));
    }

    private sealed record Fixture(
        int OwnerId,
        long ZeroPetId,
        long ProgressedPetId);

    private sealed record BeforeSnapshot(
        string ZeroPetStableJson,
        string ZeroStatsStableJson,
        string ProgressedPetJson,
        string ProgressedStatsJson);
}
