using Godswar.Server.Application.Guilds;
using Npgsql;

namespace Godswar.Server.Infrastructure.Guilds;

/// <summary>
/// Reads the guild one character belongs to, in the shape the client's window
/// renders.
/// </summary>
internal sealed partial class PostgresGuildStore
{
    public async Task<GuildSnapshot?> TryReadGuildAsync(
        int characterId,
        CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(
            cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            SELECT
                guild.id,
                guild.name,
                guild.level,
                guild.member_limit,
                guild.silver,
                guild.gold,
                guild.bijou,
                guild.proclamation,
                member.duty,
                member.character_id,
                character.name,
                character.fighter_job_lv,
                character.profession,
                member.contribution_gold,
                member.contribution_silver
            FROM public.guild_members AS member
            INNER JOIN public.guilds AS guild
                ON guild.id = member.guild_id
            INNER JOIN public.character_base AS character
                ON character.id = member.character_id
            WHERE member.guild_id = (
                SELECT guild_id
                FROM public.guild_members
                WHERE character_id = @characterId)
            ORDER BY member.duty DESC, member.character_id;
            """,
            connection);
        command.Parameters.AddWithValue("characterId", characterId);

        GuildSnapshot? guild = null;
        var members = new List<GuildMemberSnapshot>();
        // The roster reader is scoped: the buildings are a second command on the
        // same connection, and Npgsql refuses one while a reader is open
        // (measured: NpgsqlOperationInProgressException on the login push).
        await using (var reader = await command.ExecuteReaderAsync(
            cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                guild ??= new GuildSnapshot(
                    reader.GetInt64(0),
                    reader.GetString(1),
                    checked((byte)reader.GetInt32(2)),
                    reader.GetInt32(3),
                    reader.GetInt64(4),
                    reader.GetInt64(5),
                    reader.GetInt32(6),
                    reader.GetString(7),
                    members,
                    Buildings: []);
                members.Add(new GuildMemberSnapshot(
                    reader.GetInt32(9),
                    reader.GetString(10),
                    checked((short)reader.GetInt32(11)),
                    checked((byte)reader.GetInt16(8)),
                    checked((byte)reader.GetInt16(12)),
                    ContributionGold: reader.GetInt64(13),
                    ContributionSilver: reader.GetInt64(14)));
            }
        }

        if (guild is not null)
        {
            guild = guild with
            {
                Buildings = await ReadBuildingsAsync(
                    connection,
                    guild.GuildId,
                    cancellationToken)
            };
        }

        return guild;
    }

    /// <summary>
    /// Reads the guild's buildings, which the base info's own array carries.
    /// </summary>
    private static async Task<IReadOnlyList<GuildBuildingSnapshot>>
        ReadBuildingsAsync(
            NpgsqlConnection connection,
            long guildId,
            CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT building_type, level
            FROM public.guild_buildings
            WHERE guild_id = @guildId
            ORDER BY slot;
            """,
            connection);
        command.Parameters.AddWithValue("guildId", guildId);
        var buildings = new List<GuildBuildingSnapshot>();
        await using var reader = await command.ExecuteReaderAsync(
            cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            buildings.Add(new GuildBuildingSnapshot(
                reader.GetInt32(0),
                checked((byte)reader.GetInt16(1))));
        }

        return buildings;
    }
}
