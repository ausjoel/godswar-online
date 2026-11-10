using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

/// <summary>
/// Diagnostic: reports exactly how a live database's migration history differs
/// from this build's registered catalog.
/// </summary>
/// <remarks>
/// The server refuses to start when the history is not an exact ordered prefix
/// with matching checksums, and its failure event carries no message, so this
/// check exists to name the migration that diverged. It never writes: it only
/// reads the history and compares it with the catalog. Run it by pointing
/// <c>GODSWAR_MIGRATION_DIAGNOSTIC_CONNECTION_STRING</c> at the database.
/// </remarks>
internal static class PostgresMigrationHistoryDiagnosticChecks
{
    public const string CheckName = "PostgreSQL migration history diagnostic";

    private const string ConnectionStringVariable =
        "GODSWAR_MIGRATION_DIAGNOSTIC_CONNECTION_STRING";

    public static async Task RunAsync()
    {
        var connectionString =
            Environment.GetEnvironmentVariable(ConnectionStringVariable);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new CheckSkippedException(
                $"{CheckName} ({ConnectionStringVariable} is not set)");
        }

        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var applied = new List<AppliedPostgresSchemaMigration>();
        await using (var command = dataSource.CreateCommand(
            """
            SELECT migration_id, checksum
            FROM public.schema_migrations
            ORDER BY migration_id;
            """))
        {
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                applied.Add(new AppliedPostgresSchemaMigration(
                    reader.GetString(0),
                    reader.GetString(1)));
            }
        }

        var registered = PostgresSchemaMigrationCatalog.All;
        Console.WriteLine(
            $"[migration-diagnostic] registered={registered.Count} " +
            $"applied={applied.Count}");

        var registeredById = registered.ToDictionary(
            static migration => migration.Id,
            StringComparer.Ordinal);
        var reported = 0;
        for (var index = 0; index < applied.Count; index++)
        {
            var entry = applied[index];
            if (index >= registered.Count)
            {
                Console.WriteLine(
                    $"[migration-diagnostic] applied-but-not-registered " +
                    $"position={index + 1} id={entry.Id}");
                reported++;
                continue;
            }

            var expected = registered[index];
            if (!string.Equals(entry.Id, expected.Id, StringComparison.Ordinal))
            {
                Console.WriteLine(
                    $"[migration-diagnostic] prefix-mismatch position={index + 1} " +
                    $"applied={entry.Id} registered={expected.Id}");
                reported++;
                continue;
            }

            if (!string.Equals(entry.Checksum, expected.Checksum, StringComparison.Ordinal))
            {
                Console.WriteLine(
                    $"[migration-diagnostic] checksum-mismatch position={index + 1} " +
                    $"id={entry.Id} database={entry.Checksum} registered={expected.Checksum}");
                reported++;
            }
        }

        foreach (var migration in registered)
        {
            _ = registeredById;
            if (applied.All(entry =>
                    !string.Equals(entry.Id, migration.Id, StringComparison.Ordinal)))
            {
                Console.WriteLine(
                    $"[migration-diagnostic] not-applied id={migration.Id}");
            }
        }

        Console.WriteLine(
            $"[migration-diagnostic] divergences={reported}");
    }
}
