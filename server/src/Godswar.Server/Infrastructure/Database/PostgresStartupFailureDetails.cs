using Npgsql;

namespace Godswar.Server.Infrastructure.Database;

internal readonly record struct PostgresStartupFailureDetails(
    Exception Cause,
    string? SqlState,
    string? MigrationId)
{
    public static PostgresStartupFailureDetails FromException(Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);
        var cause = error;
        string? migrationId = null;
        for (var depth = 0; depth < 8; depth++)
        {
            migrationId ??= cause.Data["GodswarMigrationId"] as string;
            if (cause.InnerException is null || cause is PostgresException)
            {
                break;
            }
            cause = cause.InnerException;
        }
        return new(cause,
            cause is PostgresException postgres ? postgres.SqlState : null,
            migrationId);
    }
}
