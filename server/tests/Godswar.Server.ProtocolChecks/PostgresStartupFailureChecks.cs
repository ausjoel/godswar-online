using System.Text.Json;
using Godswar.Server.Infrastructure.Database;
using Godswar.Server.Operations;
using Godswar.Server.Operations.Observability;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static class PostgresStartupFailureChecks
{
    public static Task RunAsync()
    {
        foreach (var sqlState in new[] { "28P01", "42P01", "23505" })
        {
            Check.True(!PostgresSchemaStartup.IsTransient(DatabaseError(sqlState)),
                $"permanent PostgreSQL failure {sqlState} fails immediately");
        }
        Check.True(PostgresSchemaStartup.IsTransient(DatabaseError("40001")),
            "serialization failure is retryable");
        Check.True(PostgresSchemaStartup.IsTransient(
                new NpgsqlException("unavailable", new IOException("socket closed"))),
            "database connection failure is retryable");
        Check.True(PostgresSchemaStartup.IsTransient(new TimeoutException()),
            "connection timeout is retryable");
        Check.True(!PostgresSchemaStartup.IsTransient(new OperationCanceledException()) &&
            !PostgresSchemaStartup.IsTransient(new IOException("permanent file error")),
            "cancellation and unrelated local file errors are not availability retries");
        CheckSafeDiagnostic();
        return Task.CompletedTask;
    }

    private static PostgresException DatabaseError(string sqlState) =>
        new("password=secret-value; SELECT private_data", "ERROR", "ERROR", sqlState);

    private static void CheckSafeDiagnostic()
    {
        Exception error;
        try
        {
            throw DatabaseError("42P01");
        }
        catch (PostgresException cause)
        {
            error = new InvalidOperationException("Host=private-endpoint", cause);
            error.Data["GodswarMigrationId"] = "20260907_141";
        }
        using var sink = new StringWriter();
        using var logger = new BoundedStructuredLogger(sink);
        StartupFailureDiagnostics.Record(logger, error);
        Check.True(logger.WaitUntilIdle(TimeSpan.FromSeconds(2)),
            "startup diagnosis is flushed");
        var output = sink.ToString();
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        using var document = JsonDocument.Parse(lines[0]);
        var fields = document.RootElement;
        Check.Equal("startup_failure", fields.GetProperty("event").GetString()!,
            "terminal startup failure has a dedicated event");
        Check.Equal("postgresexception", fields.GetProperty("exception_type").GetString()!,
            "provider failure type survives wrapping");
        Check.Equal("42p01", fields.GetProperty("sqlstate").GetString()!,
            "SQLSTATE survives wrapping");
        Check.Equal("20260907_141", fields.GetProperty("migration_id").GetString()!,
            "migration identity survives wrapping");
        Check.True(lines.Length is >= 2 and <= 9 && output.Contains("startup_failure_frame"),
            "bounded method stack survives terminal startup handling");
        Check.True(!output.Contains("secret-value") && !output.Contains("private_data") &&
            !output.Contains("private-endpoint") && !output.Contains("C:\\"),
            "diagnostics omit raw SQL, connection details, exception messages, and paths");
    }
}
