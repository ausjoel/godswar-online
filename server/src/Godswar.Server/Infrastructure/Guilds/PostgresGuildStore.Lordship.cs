using Npgsql;

namespace Godswar.Server.Infrastructure.Guilds;

/// <summary>
/// GUILD (2026-11-03): handing the lordship to another member.
/// </summary>
/// <remarks>
/// The guide (igg-acid=67): "There can be only one lord in a guild ... If the
/// lord wants to delete his character or quit the guild, he should pass his
/// position to another first." The 2008 server did it in
/// <c>CPlayer::AppointDuty</c> (GameServer/GameCore/Player.cpp): a Lord who
/// appoints somebody to CONSORTIA_CHAIRMAN becomes CONSORTIA_MEMBER (duty 2)
/// himself. Both membership rows, both characters and
/// <c>guilds.lord_character_id</c> change in one transaction, so the guild never
/// has two lords or none.
/// </remarks>
internal sealed partial class PostgresGuildStore
{
    /// <summary>The duty the old Lord takes, as in the 2008 server.</summary>
    public const short FormerLordDuty = 2;

    /// <returns>Whether the hand-over was written.</returns>
    public async Task<bool> TransferLordshipAsync(
        long guildId,
        int lordCharacterId,
        int newLordCharacterId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (lordCharacterId == newLordCharacterId)
        {
            return false;
        }

        await using var connection = await _dataSource.OpenConnectionAsync(
            cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(
            cancellationToken);
        // REVIEW FIX (2026-11-03): lock the guild row first, in a statement of
        // its own. A leave or kick (RemoveMemberAsync) takes the same lock, so
        // the two serialize. The member rows are then read and locked in a NEW
        // statement: under READ COMMITTED its snapshot is taken after the guild
        // lock was granted, so a removal that committed while we waited is seen.
        await using (var guildLock = new NpgsqlCommand(
            """
            SELECT 1 FROM public.guilds WHERE id = @guildId FOR UPDATE;
            """,
            connection,
            transaction))
        {
            guildLock.Parameters.AddWithValue("guildId", guildId);
            if (await guildLock.ExecuteScalarAsync(cancellationToken) is null)
            {
                return false;
            }
        }

        var lordFound = false;
        var heirFound = false;
        await using (var check = new NpgsqlCommand(
            """
            SELECT character_id, duty FROM public.guild_members
            WHERE guild_id = @guildId AND character_id IN (@lord, @heir)
            FOR UPDATE;
            """,
            connection,
            transaction))
        {
            check.Parameters.AddWithValue("guildId", guildId);
            check.Parameters.AddWithValue("lord", lordCharacterId);
            check.Parameters.AddWithValue("heir", newLordCharacterId);
            await using var reader = await check.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var characterId = reader.GetInt32(0);
                var duty = Convert.ToInt32(reader.GetValue(1));
                if (characterId == lordCharacterId && duty == 6)
                {
                    lordFound = true;
                }
                else if (characterId == newLordCharacterId)
                {
                    heirFound = true;
                }
            }
        }

        if (!lordFound || !heirFound)
        {
            return false; // disposal rolls the transaction back
        }

        // Each write must touch exactly one row; a multi-statement command would
        // only report the sum, so they run one by one.
        if (!await ExecuteOneRowAsync(
                connection, transaction,
                """
                UPDATE public.guilds
                SET lord_character_id = @heir, updated_at = @now
                WHERE id = @guildId;
                """,
                guildId, lordCharacterId, newLordCharacterId, now, cancellationToken) ||
            !await ExecuteOneRowAsync(
                connection, transaction,
                """
                UPDATE public.guild_members SET duty = 6
                WHERE guild_id = @guildId AND character_id = @heir;
                """,
                guildId, lordCharacterId, newLordCharacterId, now, cancellationToken) ||
            !await ExecuteOneRowAsync(
                connection, transaction,
                """
                UPDATE public.guild_members SET duty = @formerDuty
                WHERE guild_id = @guildId AND character_id = @lord;
                """,
                guildId, lordCharacterId, newLordCharacterId, now, cancellationToken))
        {
            return false; // disposal rolls the transaction back
        }

        await WriteCharacterGuildAsync(
            connection, transaction, newLordCharacterId, 6, guildId,
            contribution: null, cancellationToken);
        await WriteCharacterGuildAsync(
            connection, transaction, lordCharacterId, FormerLordDuty, guildId,
            contribution: null, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    private static async Task<bool> ExecuteOneRowAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql,
        long guildId,
        int lordCharacterId,
        int newLordCharacterId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("guildId", guildId);
        command.Parameters.AddWithValue("heir", newLordCharacterId);
        command.Parameters.AddWithValue("lord", lordCharacterId);
        command.Parameters.AddWithValue("formerDuty", FormerLordDuty);
        command.Parameters.AddWithValue("now", now);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }
}
