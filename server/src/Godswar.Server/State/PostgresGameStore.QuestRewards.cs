using Npgsql;

namespace Godswar.Server.State;

/// <summary>The character's wallet after a quest reward was credited.</summary>
internal sealed record CharacterWalletResult(int Silver, int Gold);

internal sealed partial class PostgresGameStore
{
    /// <summary>
    /// Credits quest reward money and returns the balances it produced.
    /// </summary>
    /// <remarks>
    /// The update is a single transaction on the character's own row, the same
    /// shape the forge already uses when it charges silver
    /// (<c>PostgresGameStore.Crafting.cs</c>). No row is written to
    /// <c>character_currency_ledger</c>: that table requires a command-inbox entry
    /// and a wallet revision, which is the purchase path's machinery, not a quest
    /// reward's. What matters here is that the new balance reaches the database
    /// before the client is told anything, so a relog cannot undo the reward.
    /// </remarks>
    public async Task<CharacterWalletResult?> GrantQuestCurrencyAsync(
        int accountId,
        int characterId,
        int silver,
        int gold,
        CancellationToken cancellationToken = default)
    {
        silver = Math.Max(0, silver);
        gold = Math.Max(0, gold);
        if (silver == 0 && gold == 0)
        {
            return null;
        }

        await using var connection =
            await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            UPDATE character_base
            -- ROUND2: saturate at the int column instead of failing the hand-in.
            SET "Money" = LEAST("Money"::bigint + @silver, 2147483647)::integer,
                "Stone" = LEAST("Stone"::bigint + @gold, 2147483647)::integer
            WHERE account_id = @accountId
              AND id = @characterId
            RETURNING "Money", "Stone";
            """, connection);
        command.Parameters.AddWithValue("silver", silver);
        command.Parameters.AddWithValue("gold", gold);
        command.Parameters.AddWithValue("accountId", accountId);
        command.Parameters.AddWithValue("characterId", characterId);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new CharacterWalletResult(
            reader.GetInt32(0),
            reader.GetInt32(1));
    }

    /// <summary>FINALISE2 (F-19): one atomic debit ("Money" &gt;= fee), the same row shape the forge charges.</summary>
    public async Task<CharacterWalletResult?> ChargeSilverAsync(
        int accountId,
        int characterId,
        int silver,
        CancellationToken cancellationToken = default)
    {
        if (silver <= 0)
        {
            return null;
        }

        await using var connection =
            await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            UPDATE character_base
            SET "Money" = "Money" - @silver
            WHERE account_id = @accountId
              AND id = @characterId
              AND "Money" >= @silver
            RETURNING "Money", "Stone";
            """, connection);
        command.Parameters.AddWithValue("silver", silver);
        command.Parameters.AddWithValue("accountId", accountId);
        command.Parameters.AddWithValue("characterId", characterId);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new CharacterWalletResult(
            reader.GetInt32(0),
            reader.GetInt32(1));
    }
}
