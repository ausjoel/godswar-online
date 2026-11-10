using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.Infrastructure.Database;

/// <summary>
/// Owns the bounded PostgreSQL schema-startup policy independently from
/// gameplay repositories. Callers must complete this step before composing
/// adapters that assume the current schema.
/// </summary>
internal static class PostgresSchemaStartup
{
    private const int MaximumAttempts = 30;

    public static async Task InitializeAsync(
        string connectionString,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        await using var dataSource =
            NpgsqlDataSource.Create(connectionString);
        await InitializeAsync(dataSource, cancellationToken);
    }

    public static async Task InitializeAsync(
        NpgsqlDataSource dataSource,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        var runner = new PostgresSchemaMigrationRunner(dataSource);
        for (var attempt = 1; attempt <= MaximumAttempts; attempt++)
        {
            try
            {
                await runner.InitializeGodswarSchemaAsync(
                    cancellationToken);
                return;
            }
            catch (Exception error) when (
                attempt < MaximumAttempts &&
                IsTransient(error))
            {
                var details = PostgresStartupFailureDetails.FromException(error);
                var sqlState = details.SqlState is { Length: 5 } code &&
                    code.All(char.IsAsciiLetterOrDigit) ? code : "none";
                Console.WriteLine(
                    "[db] waiting for PostgreSQL schema " +
                    $"({attempt}/{MaximumAttempts}): " +
                    $"{details.Cause.GetType().Name}; sqlstate={sqlState}");
                await Task.Delay(
                    TimeSpan.FromSeconds(1),
                    cancellationToken);
            }
        }
    }

    internal static bool IsTransient(Exception error) => error switch
    {
        OperationCanceledException => false,
        NpgsqlException databaseError => databaseError.IsTransient,
        TimeoutException => true,
        _ => error.InnerException is not null && IsTransient(error.InnerException)
    };
}
