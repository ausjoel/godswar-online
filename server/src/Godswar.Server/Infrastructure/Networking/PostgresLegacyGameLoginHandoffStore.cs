using Godswar.Server.Networking;
using Npgsql;

namespace Godswar.Server.Infrastructure.Networking;

// ROUND-SHAREDHANDOFF (issue #34): the legacy game-login handoff in PostgreSQL, so a login gateway in one process
// can admit a game login in another process of the same realm set. Bound to account, selected realm and normalized
// address; expiring by the database clock; bounded in size; used at most once (DELETE ... RETURNING).
internal sealed class PostgresLegacyGameLoginHandoffStore : ILegacyGameLoginHandoffStore
{
    /// <summary>Above this many live records new ones are refused (the player sees a failed login), never trimmed.</summary>
    internal const int MaximumRecords = 50_000;

    private readonly NpgsqlDataSource _dataSource;

    public PostgresLegacyGameLoginHandoffStore(NpgsqlDataSource dataSource) =>
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));

    public string Name => "postgres";

    public async Task VerifyAsync(CancellationToken cancellationToken)
    {
        await using var command = _dataSource.CreateCommand(
            "SELECT count(*) FROM public.legacy_game_login_handoffs WHERE false;");
        await command.ExecuteScalarAsync(cancellationToken);
    }

    public async Task<bool> IssueAsync(
        long accountId, int realmId, string address, TimeSpan lifetime, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var sweep = new NpgsqlCommand(
            "DELETE FROM public.legacy_game_login_handoffs WHERE expires_at <= now();", connection, transaction))
        {
            await sweep.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var command = new NpgsqlCommand(
            """
            INSERT INTO public.legacy_game_login_handoffs (account_id, realm_id, address, issued_at, expires_at)
            SELECT @account, @realm, @address, now(), now() + @lifetime
            WHERE (SELECT count(*) FROM public.legacy_game_login_handoffs) < @maximum
            ON CONFLICT (account_id, realm_id, address)
            DO UPDATE SET issued_at = EXCLUDED.issued_at, expires_at = EXCLUDED.expires_at;
            """, connection, transaction);
        command.Parameters.AddWithValue("account", accountId);
        command.Parameters.AddWithValue("realm", realmId);
        command.Parameters.AddWithValue("address", address);
        command.Parameters.AddWithValue("lifetime", lifetime);
        command.Parameters.AddWithValue("maximum", (long)MaximumRecords);
        var written = await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return written == 1;
    }

    public async Task<bool> TryConsumeAsync(
        long accountId, int realmId, string address, CancellationToken cancellationToken)
    {
        await using var command = _dataSource.CreateCommand(
            """
            DELETE FROM public.legacy_game_login_handoffs
            WHERE account_id = @account AND realm_id = @realm AND address = @address
            RETURNING expires_at > now();
            """);
        command.Parameters.AddWithValue("account", accountId);
        command.Parameters.AddWithValue("realm", realmId);
        command.Parameters.AddWithValue("address", address);
        return await command.ExecuteScalarAsync(cancellationToken) is true;
    }
}
