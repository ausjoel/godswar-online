using Godswar.Server.Application.Guilds;
using Npgsql;

namespace Godswar.Server.Infrastructure.Guilds;

/// <summary>
/// Reads the guilds one camp has, which is what the guild window's List tab
/// shows.
/// </summary>
/// <remarks>
/// A guild has no camp column of its own: it belongs to the camp of its lord
/// (<c>character_base.camp</c>), so the filter is a join on the lord. The member
/// count is read from <c>guild_members</c> rather than from
/// <c>guilds.online_count</c>, which is only the online tally.
/// </remarks>
internal sealed partial class PostgresGuildStore
{
    public async Task<IReadOnlyList<GuildListEntry>> TryReadCampGuildsAsync(
        byte camp,
        CancellationToken cancellationToken)
    {
        await using var command = _dataSource.CreateCommand(
            """
            SELECT
                guild.id,
                guild.name,
                guild.level,
                guild.member_limit,
                guild.silver,
                guild.gold,
                guild.bijou,
                guild.online_count,
                lord.name,
                (
                    SELECT count(*)
                    FROM public.guild_members AS member
                    WHERE member.guild_id = guild.id
                ),
                (
                    SELECT count(*)
                    FROM public.guild_buildings AS building
                    WHERE building.guild_id = guild.id
                ),
                (
                    -- The footstone is building type 1; the List tab prints its
                    -- level beside the count.
                    SELECT COALESCE(max(building.level), 0)
                    FROM public.guild_buildings AS building
                    WHERE building.guild_id = guild.id
                      AND building.building_type = 1
                ),
                guild.refuse_applications
            FROM public.guilds AS guild
            INNER JOIN public.character_base AS lord
                ON lord.id = guild.lord_character_id
            WHERE lord.camp = @camp
            ORDER BY guild.id;
            """);
        command.Parameters.AddWithValue("camp", (short)camp);
        var guilds = new List<GuildListEntry>();
        await using var reader = await command.ExecuteReaderAsync(
            cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            guilds.Add(new GuildListEntry(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(8),
                checked((byte)reader.GetInt32(2)),
                checked((int)reader.GetInt64(9)),
                reader.GetInt32(3),
                reader.GetInt32(7),
                reader.GetInt64(4),
                reader.GetInt64(5),
                reader.GetInt32(6),
                checked((int)reader.GetInt64(11)),
                checked((int)reader.GetInt64(10)),
                reader.GetBoolean(12)));
        }

        return guilds;
    }
}
