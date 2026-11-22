using Npgsql;

namespace Godswar.Server.State;

/// <summary>B22 (d32): the three wallet balances after a teleporter fee or its refund.</summary>
internal sealed record TeleportWalletResult(int Silver, int Gold, int BindingGold);

internal sealed partial class PostgresGameStore
{
    // B22 (d32): one atomic debit or credit on the character's own row, the shape ChargeSilverAsync uses.
    public async Task<TeleportWalletResult?> AdjustTeleportWalletAsync(
        int accountId,
        int characterId,
        string currency,
        int delta,
        CancellationToken cancellationToken = default)
    {
        var column = currency switch
        {
            "silver" => "\"Money\"",
            "gold" => "\"Stone\"",
            "bgold" => "\"BindingGold\"",
            _ => null
        };
        if (column is null || delta == 0)
        {
            return null;
        }

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand($"""
            UPDATE character_base
            SET {column} = LEAST({column}::bigint + @delta, 2147483647)::integer
            WHERE account_id = @accountId
              AND id = @characterId
              AND {column}::bigint + @delta >= 0
            RETURNING "Money", "Stone", "BindingGold";
            """, connection);
        command.Parameters.AddWithValue("delta", (long)delta);
        command.Parameters.AddWithValue("accountId", accountId);
        command.Parameters.AddWithValue("characterId", characterId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new TeleportWalletResult(reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2));
    }
}
