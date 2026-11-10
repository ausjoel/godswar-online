using Npgsql;

namespace Godswar.Server.Infrastructure.Arena;

/// <summary>
/// ARENA build: the Gold (legacy column "Stone") charge of an AFK auto-revival.
/// One conditional UPDATE: it only succeeds when the balance covers the price, so
/// two racing revivals can never take the Gold twice or drive it below zero.
/// wallet_revision is bumped like every other wallet writer. No table is added.
/// </summary>
internal static class AfkRevivalWallet
{
    private static NpgsqlDataSource? _dataSource;

    public static bool Ready => _dataSource is not null;

    /// <summary>Start-up wiring; never stops the server.</summary>
    public static Task InitializeAsync(string connectionString)
    {
        try
        {
            _dataSource ??= NpgsqlDataSource.Create(connectionString);
        }
        catch (Exception error)
        {
            Console.WriteLine($"[afk-revive] wallet unavailable: {error.Message}");
        }
        return Task.CompletedTask;
    }

    /// <summary>The new Gold balance, or null when the character cannot pay.</summary>
    public static async Task<int?> TryDebitGoldAsync(
        int characterId,
        int accountId,
        int gold,
        CancellationToken cancellationToken)
    {
        if (_dataSource is null || gold < 0)
        {
            return null;
        }

        await using var command = _dataSource.CreateCommand(
            """
            UPDATE public.character_base
            SET "Stone" = "Stone" - @gold,
                wallet_revision = wallet_revision + 1
            WHERE id = @id AND account_id = @account
              AND lifecycle_state = 'active' AND "Stone" >= @gold
            RETURNING "Stone";
            """);
        command.Parameters.AddWithValue("gold", gold);
        command.Parameters.AddWithValue("id", characterId);
        command.Parameters.AddWithValue("account", accountId);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is int balance ? balance : null;
    }

    /// <summary>Gives back a charge whose revival could not happen. Returns the new balance.</summary>
    public static async Task<int?> RefundGoldAsync(
        int characterId,
        int accountId,
        int gold,
        CancellationToken cancellationToken)
    {
        if (_dataSource is null || gold <= 0)
        {
            return null;
        }

        await using var command = _dataSource.CreateCommand(
            """
            UPDATE public.character_base
            SET "Stone" = LEAST("Stone"::bigint + @gold, 2147483647)::integer,
                wallet_revision = wallet_revision + 1
            WHERE id = @id AND account_id = @account
            RETURNING "Stone";
            """);
        command.Parameters.AddWithValue("gold", gold);
        command.Parameters.AddWithValue("id", characterId);
        command.Parameters.AddWithValue("account", accountId);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is int balance ? balance : null;
    }
}
