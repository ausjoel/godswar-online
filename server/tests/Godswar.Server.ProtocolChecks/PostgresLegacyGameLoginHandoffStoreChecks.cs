using Godswar.Server.Infrastructure.Database;
using Godswar.Server.Infrastructure.Networking;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

// ROUND-SHAREDHANDOFF (issue #34): the shared handoff store against a disposable database.
internal static class PostgresLegacyGameLoginHandoffStoreChecks
{
    public const string CheckName = "PostgreSQL shared legacy game login handoff store";
    private const string ConnectionStringVariable = "GODSWAR_TEST_POSTGRES_CONNECTION_STRING";

    public static async Task RunAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable(ConnectionStringVariable);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new CheckSkippedException($"{CheckName} ({ConnectionStringVariable} is not set)");
        }

        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var database = (string)(await dataSource.CreateCommand("SELECT current_database();").ExecuteScalarAsync())!;
        if (!database.StartsWith("godswar_b12_handoff", StringComparison.Ordinal) && !database.StartsWith("it_", StringComparison.Ordinal))
        {
            throw new CheckSkippedException($"{CheckName} requires a disposable database (godswar_b12_handoff* or it_*); received '{database}'");
        }

        await PostgresSchemaStartup.InitializeAsync(connectionString);
        await using (var clear = dataSource.CreateCommand("DELETE FROM public.legacy_game_login_handoffs;"))
        {
            await clear.ExecuteNonQueryAsync();
        }

        var a = new PostgresLegacyGameLoginHandoffStore(dataSource);
        var b = new PostgresLegacyGameLoginHandoffStore(NpgsqlDataSource.Create(connectionString)); // "another process"
        var token = CancellationToken.None;
        await a.VerifyAsync(token);
        var life = TimeSpan.FromMinutes(2);

        Check.True(await a.IssueAsync(7, 2, "1.2.3.4", life, token), "the gateway records a handoff for realm 2");
        Check.True(!await b.TryConsumeAsync(7, 1, "1.2.3.4", token), "a realm-1 worker refuses a realm-2 handoff");
        Check.True(!await b.TryConsumeAsync(7, 2, "5.6.7.8", token), "another address is refused");
        Check.True(!await b.TryConsumeAsync(8, 2, "1.2.3.4", token), "another account is refused");
        Check.True(await b.TryConsumeAsync(7, 2, "1.2.3.4", token), "the realm-2 worker in another process admits it");
        Check.True(!await b.TryConsumeAsync(7, 2, "1.2.3.4", token), "a replay is refused (used once)");

        Check.True(await a.IssueAsync(7, 2, "1.2.3.4", TimeSpan.FromSeconds(1), token), "a short handoff is recorded");
        await Task.Delay(TimeSpan.FromSeconds(1.5));
        Check.True(!await b.TryConsumeAsync(7, 2, "1.2.3.4", token), "an expired handoff is refused");

        Check.True(await a.IssueAsync(9, 1, "9.9.9.9", life, token), "a handoff for a race");
        var race = await Task.WhenAll(
            a.TryConsumeAsync(9, 1, "9.9.9.9", token), b.TryConsumeAsync(9, 1, "9.9.9.9", token),
            a.TryConsumeAsync(9, 1, "9.9.9.9", token), b.TryConsumeAsync(9, 1, "9.9.9.9", token));
        Check.Equal(1, race.Count(static admitted => admitted), "two processes racing: exactly one admits");

        var refusedLong = false;
        try { await a.IssueAsync(10, 1, "1.1.1.1", TimeSpan.FromHours(1), token); }
        catch (PostgresException) { refusedLong = true; }
        Check.True(refusedLong, "a lifetime beyond 15 minutes is refused by the table");

        await using (var fill = dataSource.CreateCommand(
            "INSERT INTO public.legacy_game_login_handoffs (account_id, realm_id, address, expires_at) " +
            "SELECT g, 1, '10.0.0.1', now() + interval '5 minutes' FROM generate_series(1000000, 1000000 + @n - 1) g;"))
        {
            fill.Parameters.AddWithValue("n", PostgresLegacyGameLoginHandoffStore.MaximumRecords);
            await fill.ExecuteNonQueryAsync();
        }

        Check.True(!await a.IssueAsync(11, 1, "1.1.1.1", life, token), "a full store refuses new handoffs instead of trimming");
        Check.True(!await b.TryConsumeAsync(11, 1, "1.1.1.1", token), "a refused handoff never admits");
        await using (var clear = dataSource.CreateCommand("DELETE FROM public.legacy_game_login_handoffs;"))
        {
            await clear.ExecuteNonQueryAsync();
        }
    }
}
