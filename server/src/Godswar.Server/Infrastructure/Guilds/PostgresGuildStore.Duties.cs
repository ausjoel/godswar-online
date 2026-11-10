using Npgsql;

namespace Godswar.Server.Infrastructure.Guilds;

/// <summary>
/// The one place a character's duty changes.
/// </summary>
/// <remarks>
/// A duty lives twice on purpose: on the membership row, which is what the guild
/// window lists, and on the character, which is what the attribute panel's
/// 公会职位 row and the login snapshot carry. Every change - joining, leaving,
/// being promoted, being demoted, being removed - goes through these methods so
/// the two can never disagree: each one writes both rows inside a single
/// transaction and refuses to report success unless both were touched.
/// </remarks>
internal sealed partial class PostgresGuildStore
{
    /// <summary>
    /// Sets a member's duty, or seats a character in a guild at that duty.
    /// </summary>
    public async Task SetMemberDutyAsync(
        int characterId,
        long guildId,
        short duty,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (duty < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(duty),
                "A guild duty cannot be negative.");
        }

        await using var connection = await _dataSource.OpenConnectionAsync(
            cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(
            cancellationToken);

        await using (var member = new NpgsqlCommand(
            """
            INSERT INTO public.guild_members (
                character_id, guild_id, duty, joined_at)
            VALUES (
                @characterId, @guildId, @duty, @now)
            ON CONFLICT (character_id) DO UPDATE
            SET guild_id = EXCLUDED.guild_id,
                duty = EXCLUDED.duty;
            """,
            connection,
            transaction))
        {
            member.Parameters.AddWithValue("characterId", characterId);
            member.Parameters.AddWithValue("guildId", guildId);
            member.Parameters.AddWithValue("duty", duty);
            member.Parameters.AddWithValue("now", now);
            if (await member.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                throw new InvalidDataException(
                    "The guild membership duty was not stored.");
            }
        }

        await WriteCharacterGuildAsync(
            connection,
            transaction,
            characterId,
            duty,
            guildId,
            contribution: null,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>
    /// Removes a membership, whether the member left or was removed, and clears
    /// the character's guild state - all of it - in the same transaction.
    /// </summary>
    /// <remarks>
    /// Clearing the membership row alone is not enough: the character carries its
    /// own copy of the guild it belongs to, in this server's <c>guild_duty</c> and
    /// in the pair the original server filled (<c>consortia</c>,
    /// <c>consortia_job</c>) plus its own contribution
    /// (<c>consortia_contribute</c>). A character that keeps any of those after
    /// leaving is still, as far as the next reader is concerned, in a guild.
    /// </remarks>
    /// <param name="refuseLord">
    /// GUILD review fix: when true (guild.lord_rules on), the removal is refused
    /// if, under the guild row lock, the member is a Lord (duty 6) with others
    /// still in the guild - a hand-over that committed after the caller's check.
    /// </param>
    /// <returns>False only when <paramref name="refuseLord"/> refused it.</returns>
    public async Task<bool> RemoveMemberAsync(
        int characterId,
        CancellationToken cancellationToken,
        bool refuseLord = false)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(
            cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(
            cancellationToken);

        // GUILD review fix: take the guild row lock a Lord hand-over takes, so a
        // leave or kick and a hand-over never interleave.
        await using (var guildLock = new NpgsqlCommand(
            """
            SELECT g.id FROM public.guilds g
            WHERE g.id = (SELECT guild_id FROM public.guild_members
                          WHERE character_id = @characterId)
            FOR UPDATE;
            """,
            connection,
            transaction))
        {
            guildLock.Parameters.AddWithValue("characterId", characterId);
            await guildLock.ExecuteScalarAsync(cancellationToken);
        }

        if (refuseLord)
        {
            await using var lordCheck = new NpgsqlCommand(
                """
                SELECT m.duty = 6 AND (SELECT count(*) FROM public.guild_members o
                                       WHERE o.guild_id = m.guild_id) > 1
                FROM public.guild_members m
                WHERE m.character_id = @characterId;
                """,
                connection,
                transaction);
            lordCheck.Parameters.AddWithValue("characterId", characterId);
            if (await lordCheck.ExecuteScalarAsync(cancellationToken) is true)
            {
                return false; // disposal rolls the transaction back
            }
        }

        await using (var member = new NpgsqlCommand(
            """
            DELETE FROM public.guild_members
            WHERE character_id = @characterId;
            """,
            connection,
            transaction))
        {
            member.Parameters.AddWithValue("characterId", characterId);
            if (await member.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                throw new InvalidDataException(
                    "The guild membership was not removed.");
            }
        }

        await WriteCharacterGuildAsync(
            connection,
            transaction,
            characterId,
            duty: 0,
            guildId: 0,
            contribution: 0,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    /// <summary>
    /// Writes the character's own guild state: which guild it is in, at what
    /// duty, and optionally what it has contributed.
    /// </summary>
    /// <remarks>
    /// The state lives in both generations of columns on purpose - this server's
    /// <c>guild_duty</c> and the original server's <c>consortia</c> /
    /// <c>consortia_job</c> - because different readers use different ones: the
    /// guild window lists the membership row, the attribute panel and the login
    /// snapshot read the character. <paramref name="contribution"/> is nullable so
    /// that a duty change does not silently reset a member's contribution while
    /// leaving the guild still clears it.
    /// </remarks>
    private static async Task WriteCharacterGuildAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        short duty,
        long guildId,
        int? contribution,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            UPDATE public.character_base
            SET guild_duty = @duty,
                consortia_job = @duty,
                consortia = @guildId,
                consortia_contribute =
                    COALESCE(@contribution, consortia_contribute)
            WHERE id = @characterId;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue("duty", duty);
        command.Parameters.AddWithValue("guildId", guildId);
        command.Parameters.AddWithValue(
            "contribution",
            contribution is { } value ? value : DBNull.Value);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidDataException(
                "The character's guild state was not stored.");
        }
    }
}
