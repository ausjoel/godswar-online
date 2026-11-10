using Godswar.Server.Application.Guilds;
using Npgsql;

namespace Godswar.Server.Infrastructure.Guilds;

/// <summary>What a guild altar's building button does.</summary>
internal enum GuildBuildingAction
{
    Build,
    Upgrade,
    Delete
}

/// <summary>
/// The guild's buildings, as the altar's own buttons change them.
/// </summary>
/// <remarks>
/// A building is identified by its type, which is also its slot: a guild holds one
/// of each type, and the type list (<c>guild_building_types</c>, seeded from the
/// client's <c>Settings/Sys/Consortia.xml</c>) fixes both the numbering and each
/// type's own level ceiling. Upgrading therefore clamps against the type's
/// ceiling rather than against 12, because four of the seventeen types stop
/// earlier (4/5/6 at 10, the God Altar at 8).
/// </remarks>
internal sealed partial class PostgresGuildStore
{
    /// <summary>
    /// Applies one altar action to the acting character's guild and returns the
    /// guild as it now stands, or null when the character is in no guild.
    /// </summary>
    public async Task<GuildSnapshot?> TryApplyBuildingAsync(
        int characterId,
        int buildingType,
        GuildBuildingAction action,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using (var connection = await _dataSource.OpenConnectionAsync(
            cancellationToken))
        {
            await using var command = action switch
            {
                GuildBuildingAction.Build => new NpgsqlCommand(
                    """
                    INSERT INTO public.guild_buildings (
                        guild_id, slot, building_type, level, created_at, updated_at)
                    SELECT guild_id, @buildingType, @buildingType, 1, @now, @now
                    FROM public.guild_members
                    WHERE character_id = @characterId
                      AND EXISTS (
                          SELECT 1
                          FROM public.guild_building_types
                          WHERE building_type = @buildingType)
                    ON CONFLICT (guild_id, slot) DO NOTHING;
                    """,
                    connection),
                GuildBuildingAction.Upgrade => new NpgsqlCommand(
                    """
                    UPDATE public.guild_buildings
                    SET level = LEAST(
                            level + 1,
                            (SELECT maximum_level
                             FROM public.guild_building_types
                             WHERE building_type = @buildingType)),
                        updated_at = @now
                    WHERE building_type = @buildingType
                      AND guild_id = (
                          SELECT guild_id
                          FROM public.guild_members
                          WHERE character_id = @characterId)
                      AND EXISTS (
                          SELECT 1
                          FROM public.guild_building_types
                          WHERE building_type = @buildingType);
                    """,
                    connection),
                _ => new NpgsqlCommand(
                    """
                    DELETE FROM public.guild_buildings
                    WHERE building_type = @buildingType
                      AND guild_id = (
                          SELECT guild_id
                          FROM public.guild_members
                          WHERE character_id = @characterId);
                    """,
                    connection)
            };
            command.Parameters.AddWithValue("characterId", characterId);
            command.Parameters.AddWithValue("buildingType", buildingType);
            command.Parameters.AddWithValue("now", now);
            var affected = await command.ExecuteNonQueryAsync(cancellationToken);
            Console.WriteLine(
                $"[guild] building {action} character={characterId} " +
                $"type={buildingType} rows={affected}");
        }

        return await TryReadGuildAsync(characterId, cancellationToken);
    }
}
