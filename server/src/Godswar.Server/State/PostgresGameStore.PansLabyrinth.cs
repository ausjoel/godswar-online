using Npgsql;

namespace Godswar.Server.State;

/// <summary>Outcome of consuming the invitation letter and opening a Pan's Labyrinth entry.</summary>
internal enum PansLabyrinthEntryStatus : byte
{
    /// <summary>The <c>pan_labyrinth_*</c> tables are absent (feature SQL not applied).</summary>
    Unavailable = 0,
    Opened = 1,
    LetterMissing = 2
}

internal readonly record struct PansLabyrinthEntryResult(PansLabyrinthEntryStatus Status, long EntryId);

/// <summary>Outcome of one servant claim. <c>Ordinal</c> is the claim's place in its entry (1..9).</summary>
internal enum PansLabyrinthClaimStatus : byte
{
    Unavailable = 0,
    Claimed = 1,
    AlreadyClaimed = 2,
    NoEntry = 3
}

internal readonly record struct PansLabyrinthClaimResult(PansLabyrinthClaimStatus Status, int Ordinal);

/// <summary>
/// Pan's Labyrinth persistence. The two tables are created by the feature's own SQL
/// (<c>Development/pan-labyrinth/pan-labyrinth.sql</c>), not by the migration catalog,
/// so a database without them reports <see cref="PansLabyrinthEntryStatus.Unavailable"/>.
/// </summary>
internal sealed partial class PostgresGameStore
{
    private const string PansUndefinedTable = "42P01";

    /// <summary>
    /// Removes one letter from the given bag slot and opens a new entry, in one
    /// transaction, so a letter is never spent without an entry or the reverse.
    /// </summary>
    public async Task<PansLabyrinthEntryResult> OpenPansLabyrinthEntryAsync(int accountId, int characterId,
        int slot, uint letterItemId, int expectedStack, CancellationToken cancellationToken = default)
    {
        if (slot is < 0 or >= 96 || expectedStack <= 0)
            return new(PansLabyrinthEntryStatus.LetterMissing, 0);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            await using (var owner = new NpgsqlCommand("""
                SELECT id FROM character_base WHERE id=@character AND account_id=@account FOR UPDATE;
                """, connection, transaction))
            {
                owner.Parameters.AddWithValue("character", characterId);
                owner.Parameters.AddWithValue("account", accountId);
                if (await owner.ExecuteScalarAsync(cancellationToken) is null)
                    return new(PansLabyrinthEntryStatus.LetterMissing, 0);
            }

            long entryId;
            await using (var entry = new NpgsqlCommand("""
                INSERT INTO pan_labyrinth_entries(character_id, letter_item_id) VALUES (@character, @item) RETURNING id;
                """, connection, transaction))
            {
                entry.Parameters.AddWithValue("character", characterId);
                entry.Parameters.AddWithValue("item", checked((int)letterItemId));
                entryId = (long)(await entry.ExecuteScalarAsync(cancellationToken))!;
            }

            long itemRow;
            await using (var item = new NpgsqlCommand("""
                SELECT id FROM character_items WHERE user_id=@character AND item_location=1
                    AND slot_index=@slot AND prop_id=@item AND stack=@stack FOR UPDATE;
                """, connection, transaction))
            {
                item.Parameters.AddWithValue("character", characterId);
                item.Parameters.AddWithValue("slot", (short)slot);
                item.Parameters.AddWithValue("item", checked((int)letterItemId));
                item.Parameters.AddWithValue("stack", expectedStack);
                if (await item.ExecuteScalarAsync(cancellationToken) is not long row)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return new(PansLabyrinthEntryStatus.LetterMissing, 0);
                }

                itemRow = row;
            }

            if (expectedStack == 1)
            {
                await DeleteCharacterItemSlotAsync(connection, transaction, characterId,
                    ItemLocationKitBag, slot, "pan-labyrinth-letter", cancellationToken);
            }
            else
            {
                await using var consume = new NpgsqlCommand("""
                    WITH old AS (SELECT * FROM character_items WHERE id=@id),
                    changed AS (UPDATE character_items SET stack=stack-1,updated_at=now() WHERE id=@id RETURNING *)
                    INSERT INTO character_item_audit(source,action,user_id,item_location,slot_index,
                        prop_id,item_quality,item_grade,item_exp,old_item)
                    SELECT 'pan-labyrinth-letter','update',user_id,item_location,slot_index,
                        prop_id,item_quality,item_grade,item_exp,to_jsonb(old) FROM old;
                    """, connection, transaction);
                consume.Parameters.AddWithValue("id", itemRow);
                await consume.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var revision = new NpgsqlCommand("""
                UPDATE character_base SET inventory_revision=inventory_revision+1
                WHERE id=@character AND account_id=@account;
                """, connection, transaction))
            {
                revision.Parameters.AddWithValue("character", characterId);
                revision.Parameters.AddWithValue("account", accountId);
                await revision.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
            return new(PansLabyrinthEntryStatus.Opened, entryId);
        }
        catch (PostgresException error) when (error.SqlState == PansUndefinedTable)
        {
            return new(PansLabyrinthEntryStatus.Unavailable, 0);
        }
    }

    /// <summary>Marks an entry whose map transfer was refused, after its letter was handed back.</summary>
    public async Task MarkPansLabyrinthEntryRefundedAsync(long entryId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            "UPDATE pan_labyrinth_entries SET refunded=true WHERE id=@id;", connection);
        command.Parameters.AddWithValue("id", entryId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// Records servant <paramref name="servant"/> as claimed for the character's latest
    /// live entry. The primary key (entry, servant) makes a second claim a no-op.
    /// </summary>
    public async Task<PansLabyrinthClaimResult> ClaimPansLabyrinthServantAsync(int characterId, int servant,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            long entryId;
            await using (var latest = new NpgsqlCommand("""
                SELECT id FROM pan_labyrinth_entries WHERE character_id=@character AND NOT refunded
                ORDER BY id DESC LIMIT 1 FOR UPDATE;
                """, connection, transaction))
            {
                latest.Parameters.AddWithValue("character", characterId);
                if (await latest.ExecuteScalarAsync(cancellationToken) is not long id)
                    return new(PansLabyrinthClaimStatus.NoEntry, 0);
                entryId = id;
            }

            await using (var claim = new NpgsqlCommand("""
                INSERT INTO pan_labyrinth_claims(entry_id, servant) VALUES (@entry, @servant)
                ON CONFLICT DO NOTHING;
                """, connection, transaction))
            {
                claim.Parameters.AddWithValue("entry", entryId);
                claim.Parameters.AddWithValue("servant", (short)servant);
                if (await claim.ExecuteNonQueryAsync(cancellationToken) != 1)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return new(PansLabyrinthClaimStatus.AlreadyClaimed, 0);
                }
            }

            int ordinal;
            await using (var count = new NpgsqlCommand(
                "SELECT count(*)::int FROM pan_labyrinth_claims WHERE entry_id=@entry;", connection, transaction))
            {
                count.Parameters.AddWithValue("entry", entryId);
                ordinal = (int)(await count.ExecuteScalarAsync(cancellationToken))!;
            }

            await transaction.CommitAsync(cancellationToken);
            return new(PansLabyrinthClaimStatus.Claimed, ordinal);
        }
        catch (PostgresException error) when (error.SqlState == PansUndefinedTable)
        {
            return new(PansLabyrinthClaimStatus.Unavailable, 0);
        }
    }

    /// <summary>Undoes a claim whose payment failed, so the servant can be asked again.</summary>
    public async Task UnclaimPansLabyrinthServantAsync(int characterId, int servant,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            DELETE FROM pan_labyrinth_claims WHERE servant=@servant AND entry_id=(
                SELECT id FROM pan_labyrinth_entries WHERE character_id=@character AND NOT refunded
                ORDER BY id DESC LIMIT 1);
            """, connection);
        command.Parameters.AddWithValue("character", characterId);
        command.Parameters.AddWithValue("servant", (short)servant);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
