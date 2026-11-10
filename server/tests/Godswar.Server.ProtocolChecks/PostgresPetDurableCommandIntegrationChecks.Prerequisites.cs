using System.Text.RegularExpressions;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresPetDurableCommandIntegrationChecks
{
    public const string CheckName = "PostgreSQL retry-safe pet value commands";
    private const string ConnectionStringVariable =
        "GODSWAR_TEST_POSTGRES_CONNECTION_STRING";
    private static readonly Regex DisposableDatabasePattern = new(
        @"^godswar_(?:b03_[a-f0-9]{10}_smoke_[0-9]{2}|b12_[a-z0-9_]{1,40})$",
        RegexOptions.CultureInvariant);

    private static string ReadRequiredConnectionString()
    {
        var connectionString = Environment.GetEnvironmentVariable(ConnectionStringVariable);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new CheckSkippedException($"{CheckName} ({ConnectionStringVariable} is not set)");
        }
        return connectionString;
    }

    private static async Task AssertDisposableDatabaseAsync(NpgsqlDataSource dataSource)
    {
        var database = await ReadDatabaseNameAsync(dataSource);
        if (!DisposableDatabasePattern.IsMatch(database))
        {
            throw new CheckSkippedException($"{CheckName} requires a disposable B03/B12 " +
                $"database; received '{database}'");
        }
    }
}
