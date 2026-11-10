using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.Infrastructure.Guilds;

/// <summary>
/// Reads the two guild numbers the character panel prints.
/// </summary>
/// <remarks>
/// Both live on the character, not on the membership row: <c>guild_duty</c> (and
/// the original server's <c>consortia_job</c>/<c>consortia</c>, which
/// <c>PostgresGuildStore.Duties</c> writes in the same transaction) is the duty
/// the <c>Positionpro</c> row shows, and <c>consortia_contribute</c> is the
/// balance the <c>Contributepro</c> row shows - the character's own remaining
/// contribution, not the per-member totals in <c>guild_members</c>.
/// </remarks>
internal sealed partial class PostgresGuildStore
{
    /// <summary>
    /// The guild panel status one character currently has, or null when no such
    /// character exists.
    /// </summary>
    public async Task<GuildPanelStatus?> TryReadPanelStatusAsync(
        int characterId,
        CancellationToken cancellationToken)
    {
        await using var command = _dataSource.CreateCommand(
            """
            SELECT
                COALESCE(c.guild_duty, 0),
                COALESCE(c.consortia_contribute, 0),
                COALESCE(g.name, '')
            FROM public.character_base AS c
            LEFT JOIN public.guild_members AS member
                ON member.character_id = c.id
            LEFT JOIN public.guilds AS g
                ON g.id = member.guild_id
            WHERE c.id = @characterId;
            """);
        command.Parameters.AddWithValue("characterId", characterId);
        await using var reader = await command.ExecuteReaderAsync(
            cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new GuildPanelStatus(
            checked((byte)reader.GetInt16(0)),
            Math.Max(0, reader.GetInt32(1)),
            reader.GetString(2));
    }
}
