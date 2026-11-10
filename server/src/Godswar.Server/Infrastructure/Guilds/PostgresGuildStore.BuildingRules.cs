using Godswar.Server.Application.Guilds;
using Npgsql;

namespace Godswar.Server.Infrastructure.Guilds;

/// <summary>
/// GUILD (2026-11-03): the guide's building rules (igg-acid=67, "Guild
/// Buildings" and "Building Consumption"), used instead of
/// <see cref="TryApplyBuildingAsync"/> while <c>guild.building_rules</c> is on.
/// </summary>
/// <remarks>
/// <para>
/// The prices are the guide's table, which is also the client's own
/// <c>Consortia.xml</c> <c>BuildingImpact</c> block already seeded into
/// <c>guild_building_levels</c>: <c>upgrade_found</c> is the Silver row and
/// <c>upgrade_bijou</c> the Gold row (Footstone L2 100 Gold / 10,000 Silver,
/// Common Tent L1 50 / 5,000, God Altar L1 5,000 / 500,000 - all equal). The row
/// for level L is what reaching level L costs, so building pays row 1 and an
/// upgrade from L pays row L+1. They are paid from the guild purse the
/// donations fill: <c>guilds.gold</c> (Gold donations) and <c>guilds.silver</c>
/// (Silver donations).
/// </para>
/// <para>
/// Everything happens in one transaction with the guild row locked, so two
/// officers pressing at once cannot both spend the same purse. A building that
/// already stands above the Footstone (built before these rules) is kept as it
/// is; only its next upgrade waits for the Footstone.
/// </para>
/// </remarks>
internal sealed partial class PostgresGuildStore
{
    public async Task<(GuildBuildingOutcome Outcome, GuildSnapshot? Guild)>
        TryApplyBuildingWithRulesAsync(
            int characterId,
            int buildingType,
            GuildBuildingAction action,
            int buildDuty,
            int deleteDuty,
            DateTimeOffset now,
            CancellationToken cancellationToken)
    {
        GuildBuildingOutcome outcome;
        await using (var connection = await _dataSource.OpenConnectionAsync(
            cancellationToken))
        {
            await using var transaction = await connection.BeginTransactionAsync(
                cancellationToken);
            outcome = await DecideAndApplyAsync(
                connection,
                transaction,
                characterId,
                buildingType,
                action,
                buildDuty,
                deleteDuty,
                now,
                cancellationToken);
            if (outcome == GuildBuildingOutcome.Done)
            {
                await transaction.CommitAsync(cancellationToken);
            }
            else
            {
                await transaction.RollbackAsync(cancellationToken);
            }
        }

        Console.WriteLine(
            $"[guild] building {action} (rules) character={characterId} " +
            $"type={buildingType} outcome={outcome}");
        return (outcome, await TryReadGuildAsync(characterId, cancellationToken));
    }

    private static async Task<GuildBuildingOutcome> DecideAndApplyAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        int buildingType,
        GuildBuildingAction action,
        int buildDuty,
        int deleteDuty,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        long guildId;
        int duty;
        int guildLevel;
        long silver;
        long gold;
        await using (var read = new NpgsqlCommand(
            """
            SELECT g.id, m.duty, g.level, g.silver, g.gold
            FROM public.guild_members AS m
            JOIN public.guilds AS g ON g.id = m.guild_id
            WHERE m.character_id = @characterId
            FOR UPDATE OF g;
            """,
            connection,
            transaction))
        {
            read.Parameters.AddWithValue("characterId", characterId);
            await using var reader = await read.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return GuildBuildingOutcome.NotAMember;
            }

            guildId = reader.GetInt64(0);
            duty = reader.GetInt16(1);
            guildLevel = reader.GetInt32(2);
            silver = reader.GetInt64(3);
            gold = reader.GetInt64(4);
        }

        int? footstone = null;
        int? current = null;
        await using (var buildings = new NpgsqlCommand(
            """
            SELECT building_type, level
            FROM public.guild_buildings
            WHERE guild_id = @guildId
              AND building_type IN (@footstone, @buildingType);
            """,
            connection,
            transaction))
        {
            buildings.Parameters.AddWithValue("guildId", guildId);
            buildings.Parameters.AddWithValue(
                "footstone",
                GuildBuildingPolicy.FootstoneType);
            buildings.Parameters.AddWithValue("buildingType", buildingType);
            await using var reader = await buildings.ExecuteReaderAsync(
                cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var type = reader.GetInt32(0);
                var level = (int)reader.GetInt16(1);
                if (type == GuildBuildingPolicy.FootstoneType)
                {
                    footstone = level;
                }

                if (type == buildingType)
                {
                    current = level;
                }
            }
        }

        if (action == GuildBuildingAction.Delete)
        {
            if (current is null)
            {
                return GuildBuildingOutcome.NoSuchBuilding;
            }

            var deleteOutcome = GuildBuildingPolicy.DecideDelete(
                duty,
                deleteDuty,
                buildingType);
            if (deleteOutcome != GuildBuildingOutcome.Done)
            {
                return deleteOutcome;
            }

            await using var delete = new NpgsqlCommand(
                """
                DELETE FROM public.guild_buildings
                WHERE guild_id = @guildId AND building_type = @buildingType;
                """,
                connection,
                transaction);
            delete.Parameters.AddWithValue("guildId", guildId);
            delete.Parameters.AddWithValue("buildingType", buildingType);
            await delete.ExecuteNonQueryAsync(cancellationToken);
            return GuildBuildingOutcome.Done;
        }

        var building = action == GuildBuildingAction.Build;
        if (building && current is not null)
        {
            return GuildBuildingOutcome.AlreadyBuilt;
        }

        if (!building && current is null)
        {
            return GuildBuildingOutcome.NoSuchBuilding;
        }

        var target = GuildBuildingPolicy.TargetLevel(building, current ?? 0);
        (long Silver, long Gold)? cost = null;
        await using (var price = new NpgsqlCommand(
            """
            SELECT l.upgrade_found, l.upgrade_bijou
            FROM public.guild_building_levels AS l
            JOIN public.guild_building_types AS t
                ON t.building_type = l.building_type
            WHERE l.building_type = @buildingType
              AND l.level = @target
              AND l.level <= t.maximum_level;
            """,
            connection,
            transaction))
        {
            price.Parameters.AddWithValue("buildingType", buildingType);
            price.Parameters.AddWithValue("target", (short)Math.Min(target, 32767));
            await using var reader = await price.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                cost = (reader.GetInt64(0), reader.GetInt64(1));
            }
        }

        if (cost is null && building)
        {
            // A type the content table does not know (advanced altars 20-29).
            return GuildBuildingOutcome.UnknownBuilding;
        }

        var decision = GuildBuildingPolicy.DecideRaise(
            duty,
            buildDuty,
            buildingType,
            target,
            GuildBuildingPolicy.EffectiveFootstoneLevel(footstone, guildLevel),
            cost,
            silver,
            gold);
        if (decision != GuildBuildingOutcome.Done)
        {
            return decision;
        }

        var paid = cost!.Value;
        await using (var purse = new NpgsqlCommand(
            """
            UPDATE public.guilds
            SET silver = silver - @silver,
                gold = gold - @gold,
                level = CASE
                    WHEN @buildingType = @footstone THEN GREATEST(level, @target)
                    ELSE level
                END,
                updated_at = @now
            WHERE id = @guildId;
            """,
            connection,
            transaction))
        {
            purse.Parameters.AddWithValue("silver", paid.Silver);
            purse.Parameters.AddWithValue("gold", paid.Gold);
            purse.Parameters.AddWithValue("buildingType", buildingType);
            purse.Parameters.AddWithValue(
                "footstone",
                GuildBuildingPolicy.FootstoneType);
            purse.Parameters.AddWithValue("target", target);
            purse.Parameters.AddWithValue("now", now);
            purse.Parameters.AddWithValue("guildId", guildId);
            await purse.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var write = new NpgsqlCommand(
            building
                ? """
                  INSERT INTO public.guild_buildings (
                      guild_id, slot, building_type, level, created_at, updated_at)
                  VALUES (@guildId, @buildingType, @buildingType, 1, @now, @now);
                  """
                : """
                  UPDATE public.guild_buildings
                  SET level = @target, updated_at = @now
                  WHERE guild_id = @guildId AND building_type = @buildingType;
                  """,
            connection,
            transaction))
        {
            write.Parameters.AddWithValue("guildId", guildId);
            write.Parameters.AddWithValue("buildingType", buildingType);
            write.Parameters.AddWithValue("target", (short)target);
            write.Parameters.AddWithValue("now", now);
            await write.ExecuteNonQueryAsync(cancellationToken);
        }

        Console.WriteLine(
            $"[guild] building paid guild={guildId} type={buildingType} " +
            $"level={target} gold={paid.Gold} silver={paid.Silver}");
        return GuildBuildingOutcome.Done;
    }
}
