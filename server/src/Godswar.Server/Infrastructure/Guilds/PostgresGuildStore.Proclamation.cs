using Godswar.Server.Application.Guilds;
using Npgsql;

namespace Godswar.Server.Infrastructure.Guilds;

/// <summary>
/// The guild's proclamation: the one guild field a member edits by hand.
/// </summary>
/// <remarks>
/// It is stored on the guild row and published twice, because the client reads it
/// from two different messages: the placard update carries the text on its own,
/// and the base info carries it as one field of the guild record. Both are sent,
/// so a window that is open repaints and a window opened later still shows it.
/// </remarks>
internal sealed partial class PostgresGuildStore
{
    /// <summary>
    /// Writes the proclamation of the guild the character belongs to and returns
    /// the guild as it now stands, or null when the character is in none.
    /// </summary>
    public async Task<GuildSnapshot?> TrySetProclamationAsync(
        int characterId,
        string proclamation,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using (var connection = await _dataSource.OpenConnectionAsync(
            cancellationToken))
        {
            await using var command = new NpgsqlCommand(
                """
                UPDATE public.guilds
                SET proclamation = @proclamation,
                    updated_at = @now
                WHERE id = (
                    SELECT guild_id
                    FROM public.guild_members
                    WHERE character_id = @characterId);
                """,
                connection);
            command.Parameters.AddWithValue("proclamation", proclamation);
            command.Parameters.AddWithValue("now", now);
            command.Parameters.AddWithValue("characterId", characterId);
            // Zero rows means the character is in no guild: the caller answers
            // that from the null snapshot, not from a silently created guild.
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        return await TryReadGuildAsync(characterId, cancellationToken);
    }
}
