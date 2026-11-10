using Godswar.Server.Application.Guilds;
using Npgsql;

namespace Godswar.Server.Infrastructure.Guilds;

/// <summary>One altar whose balance a settlement drained.</summary>
internal readonly record struct GuildAltarWorshipSettlement(
    int CharacterId,
    int BuildingType,
    long PreviousPoints,
    long Points);

internal sealed partial class PostgresGuildStore
{
    /// <summary>
    /// Every altar the character has offered to, with the points the altar still
    /// holds for them and the level the guild has built it to.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Each altar is its own account: the level picks which of the altar's own base
    /// values applies, and the points decide how much of it is paid.
    /// </para>
    /// <para>
    /// The hourly drain is settled first, in the same transaction that locks the
    /// rows: points that have aged out must not be paid as a bonus, and a settled
    /// row is what the read is allowed to report. The rates are the user's own
    /// (see <see cref="GuildAltarDecayPolicy"/>), not the client's
    /// <c>BuildingConsume</c> table, so this no longer reads that table at all.
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyList<GuildAltarWorshipBalance>>
        TryReadAltarWorshipAsync(
            int characterId,
            DateTimeOffset now,
            CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(
            cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(
            cancellationToken);

        var balances = new List<GuildAltarWorshipBalance>();
        var writes = new List<(int BuildingType, long Points, DateTimeOffset
            SettledAt)>();
        await using (var read = new NpgsqlCommand(
            """
            SELECT
                worship.building_type,
                worship.points,
                worship.settled_at,
                building.level
            FROM public.guild_altar_worship AS worship
            INNER JOIN public.guild_buildings AS building
                ON building.guild_id = worship.guild_id
               AND building.building_type = worship.building_type
            WHERE worship.character_id = @characterId
            ORDER BY worship.building_type
            FOR UPDATE OF worship;
            """,
            connection,
            transaction))
        {
            read.Parameters.AddWithValue("characterId", characterId);
            await using var reader = await read.ExecuteReaderAsync(
                cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var buildingType = reader.GetInt32(0);
                var points = (long)reader.GetInt32(1);
                var settledAt = reader.GetFieldValue<DateTimeOffset>(2);
                var level = (int)reader.GetInt16(3);

                var (remaining, settledTo) = GuildAltarDecayPolicy.Settle(
                    points,
                    settledAt,
                    now);
                if (remaining != points || settledTo != settledAt)
                {
                    writes.Add((buildingType, remaining, settledTo));
                    Console.WriteLine(
                        $"[guild] altar decay character={characterId} " +
                        $"building={buildingType} points={points}->{remaining} " +
                        $"settled={settledAt:O}->{settledTo:O}");
                }

                balances.Add(new GuildAltarWorshipBalance(
                    buildingType,
                    remaining,
                    level));
            }
        }

        foreach (var write in writes)
        {
            await WriteSettlementAsync(
                connection,
                transaction,
                characterId,
                write.BuildingType,
                write.Points,
                write.SettledAt,
                cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return balances;
    }

    /// <summary>
    /// Drains the altars of the given characters without reading a bonus for them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is what the hourly background settlement calls. The drain has to be
    /// applied whether or not the member is online, and the lazy settlement in
    /// <see cref="TryReadAltarWorshipAsync"/> only runs when something reads an
    /// altar - so without this the stored balance is stale until the member next
    /// opens the guild window, and every offline hour lands in one lump.
    /// </para>
    /// <para>
    /// Only rows that actually moved are reported, so a caller can push a status
    /// refresh to exactly the members whose altars changed.
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyList<GuildAltarWorshipSettlement>>
        TrySettleAltarWorshipAsync(
            IReadOnlyList<int> characterIds,
            DateTimeOffset now,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(characterIds);
        if (characterIds.Count == 0)
        {
            return [];
        }

        await using var connection = await _dataSource.OpenConnectionAsync(
            cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(
            cancellationToken);

        var settlements = new List<GuildAltarWorshipSettlement>();
        var writes = new List<(int CharacterId, int BuildingType, long Points,
            DateTimeOffset SettledAt)>();
        await using (var read = new NpgsqlCommand(
            """
            SELECT
                worship.character_id,
                worship.building_type,
                worship.points,
                worship.settled_at
            FROM public.guild_altar_worship AS worship
            INNER JOIN public.guild_buildings AS building
                ON building.guild_id = worship.guild_id
               AND building.building_type = worship.building_type
            WHERE worship.character_id = ANY(@characterIds)
            ORDER BY worship.character_id, worship.building_type
            FOR UPDATE OF worship;
            """,
            connection,
            transaction))
        {
            read.Parameters.AddWithValue("characterIds", characterIds.ToArray());
            await using var reader = await read.ExecuteReaderAsync(
                cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var characterId = reader.GetInt32(0);
                var buildingType = reader.GetInt32(1);
                var points = (long)reader.GetInt32(2);
                var settledAt = reader.GetFieldValue<DateTimeOffset>(3);

                var (remaining, settledTo) = GuildAltarDecayPolicy.Settle(
                    points,
                    settledAt,
                    now);
                if (remaining == points && settledTo == settledAt)
                {
                    continue;
                }

                settlements.Add(new GuildAltarWorshipSettlement(
                    characterId,
                    buildingType,
                    points,
                    remaining));
                writes.Add((characterId, buildingType, remaining, settledTo));
            }
        }

        foreach (var write in writes)
        {
            await WriteSettlementAsync(
                connection,
                transaction,
                write.CharacterId,
                write.BuildingType,
                write.Points,
                write.SettledAt,
                cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        foreach (var settlement in settlements)
        {
            Console.WriteLine(
                $"[guild] altar decay settled character={settlement.CharacterId} " +
                $"building={settlement.BuildingType} " +
                $"points={settlement.PreviousPoints}->{settlement.Points}");
        }

        return settlements;
    }

    private static async Task WriteSettlementAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        int buildingType,
        long points,
        DateTimeOffset settledAt,
        CancellationToken cancellationToken)
    {
        await using var update = new NpgsqlCommand(
            """
            UPDATE public.guild_altar_worship
            SET points = @points,
                settled_at = @settledAt
            WHERE character_id = @characterId
              AND building_type = @buildingType;
            """,
            connection,
            transaction);
        update.Parameters.AddWithValue("points", points);
        update.Parameters.AddWithValue("settledAt", settledAt);
        update.Parameters.AddWithValue("characterId", characterId);
        update.Parameters.AddWithValue("buildingType", buildingType);
        await update.ExecuteNonQueryAsync(cancellationToken);
    }
}
