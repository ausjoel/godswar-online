using Godswar.Server.Application.WorldInstances;
using Npgsql;

namespace Godswar.Server.State;

internal sealed partial class PostgresGameStore : ILelantineFarmPointsStore
{
    /// <summary>
    /// Both camps' totals plus the asking character's own standing, in one read.
    /// </summary>
    /// <remarks>
    /// Every value comes from <c>lelantine_farm_personal_points</c>, which is
    /// the activity's one definition of a personal score: the character's
    /// donations and credited kills in one faction, summed. The faction total is
    /// therefore the sum of every same-faction personal score by construction,
    /// and the ranking figures are computed over the same rows, which is what
    /// keeps a character's rank consistent with the high score printed beside
    /// it.
    /// </remarks>
    private const string FarmScoreReadSql = """
        WITH personal AS (
            SELECT character_id, faction, points
            FROM public.lelantine_farm_personal_points
        ),
        own AS (
            SELECT COALESCE(MAX(points), 0)::bigint AS points
            FROM personal
            WHERE character_id = @characterId AND faction = @faction
        )
        SELECT
            COALESCE((
                SELECT SUM(points)
                FROM public.lelantine_farm_donations
                WHERE character_id = @characterId AND faction = @faction
            ), 0)::bigint AS donated_points,
            COALESCE((
                SELECT SUM(kill_points)
                FROM public.lelantine_farm_kill_points
                WHERE character_id = @characterId AND faction = @faction
            ), 0)::bigint AS kill_points,
            (SELECT points FROM own) AS character_points,
            COALESCE((SELECT MAX(points) FROM personal), 0)::bigint
                AS highest_points,
            (
                1 + (
                    SELECT COUNT(*)
                    FROM personal
                    WHERE points > (SELECT points FROM own)
                )
            )::bigint AS character_rank,
            COALESCE((
                SELECT SUM(points) FROM personal WHERE faction = @spartaFaction
            ), 0)::bigint AS sparta_points,
            COALESCE((
                SELECT SUM(points) FROM personal WHERE faction = @athensFaction
            ), 0)::bigint AS athens_points;
        """;

    public async Task<FarmDonationResult> DonateHoundEggsAsync(
        int accountId,
        int characterId,
        byte faction,
        int itemId,
        int? bagSlot,
        int eggCount,
        CancellationToken cancellationToken = default)
    {
        if (!LelantineFarmPointsPolicy.TryResolveTier(itemId, out _))
        {
            throw new ArgumentOutOfRangeException(
                nameof(itemId),
                "Item is not a farm donation egg.");
        }

        if (!LelantineFarmPointsPolicy.IsAcceptedQuantity(eggCount))
        {
            throw new ArgumentOutOfRangeException(nameof(eggCount));
        }

        if (bagSlot is { } submittedSlot &&
            (submittedSlot < 0 ||
             submittedSlot >= KitBagItemGrantPlanner.SlotCount))
        {
            throw new ArgumentOutOfRangeException(nameof(bagSlot));
        }

        await using var connection =
            await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction =
            await connection.BeginTransactionAsync(cancellationToken);

        await using (var command = new NpgsqlCommand("""
            SELECT true
            FROM character_base
            WHERE account_id = @accountId AND id = @characterId
            FOR UPDATE;
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("accountId", accountId);
            command.Parameters.AddWithValue("characterId", characterId);
            if (await command.ExecuteScalarAsync(cancellationToken) is null)
            {
                return new FarmDonationResult(
                    FarmDonationStatus.CharacterNotFound,
                    null,
                    0,
                    0,
                    0,
                    0);
            }
        }

        // The eggs are consumed first, under the row locks taken above, so the
        // award can never be booked against eggs the bag did not hold. Each egg
        // carries the pet aptitude it will hatch as, and the award follows that
        // aptitude, so the points come back with the consumed rows. A submission
        // names the stack the player put in the box, so the eggs - and therefore
        // the aptitude - are the ones they chose rather than whichever stack
        // happens to sit first in the bag.
        var consumption = await TryConsumeFarmEggsAsync(
            connection,
            transaction,
            characterId,
            itemId,
            bagSlot,
            eggCount,
            cancellationToken);
        if (consumption.Refusal != FarmEggRefusal.None)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new FarmDonationResult(
                consumption.Refusal switch
                {
                    FarmEggRefusal.NotEnoughEggs =>
                        FarmDonationStatus.NotEnoughEggs,
                    FarmEggRefusal.NoEggInBox =>
                        FarmDonationStatus.NoEggInBox,
                    _ => FarmDonationStatus.Unsupported
                },
                await GetCharacterByIdAsync(characterId, cancellationToken),
                0,
                0,
                0,
                0);
        }

        var pointsAwarded = consumption.Points;

        await using (var command = new NpgsqlCommand("""
            INSERT INTO lelantine_farm_donations (
                character_id, faction, item_id, egg_count, points
            )
            VALUES (@characterId, @faction, @itemId, @eggCount, @points);
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("characterId", characterId);
            command.Parameters.AddWithValue("faction", (short)faction);
            command.Parameters.AddWithValue("itemId", itemId);
            command.Parameters.AddWithValue("eggCount", eggCount);
            command.Parameters.AddWithValue("points", pointsAwarded);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        // The faction total is the sum of every personal score in the ledger, so
        // it is read back from the same rows the donation was just written to
        // rather than advanced as a second running total. SUM over a bigint
        // column is numeric, so the total is cast back to bigint: the runtime
        // reads it as an Int64 and a numeric would not cast.
        long factionPoints;
        await using (var command = new NpgsqlCommand("""
            SELECT COALESCE(SUM(points), 0)::bigint
            FROM public.lelantine_farm_personal_points
            WHERE faction = @faction;
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("faction", (short)faction);
            factionPoints = checked((long)(await command.ExecuteScalarAsync(
                cancellationToken) ?? 0L));
        }

        long characterDonated;
        await using (var command = new NpgsqlCommand("""
            SELECT COALESCE(SUM(points), 0)::bigint
            FROM lelantine_farm_donations
            WHERE character_id = @characterId AND faction = @faction;
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("characterId", characterId);
            command.Parameters.AddWithValue("faction", (short)faction);
            characterDonated = checked((long)(await command.ExecuteScalarAsync(
                cancellationToken) ?? 0L));
        }

        await transaction.CommitAsync(cancellationToken);

        return new FarmDonationResult(
            FarmDonationStatus.Donated,
            await GetCharacterByIdAsync(characterId, cancellationToken),
            eggCount,
            pointsAwarded,
            factionPoints,
            characterDonated);
    }

    /// <summary>
    /// Takes <paramref name="eggCount"/> eggs off the character's kit bag,
    /// deleting emptied stacks, and reports what they were worth.
    /// </summary>
    /// <param name="bagSlot">
    /// The stack to take them from, or <see langword="null"/> to take them in
    /// slot order from every stack of the item.
    /// </param>
    /// <remarks>
    /// Each consumed egg contributes the award for its own
    /// <c>item_quality</c>, which is the pet aptitude it will hatch as, so a
    /// stack of finer eggs is worth more than a stack of coarser ones. Every
    /// egg that would be taken is priced before anything is written, so an egg
    /// whose aptitude the activity does not score
    /// (<see cref="LelantineFarmPointsPolicy.TryResolveEggDonationPoints"/>
    /// refuses it) leaves the bag untouched and is reported as
    /// <see cref="FarmEggRefusal.UnsupportedAptitude"/>.
    /// </remarks>
    private static async Task<FarmEggConsumption> TryConsumeFarmEggsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        int itemId,
        int? bagSlot,
        int eggCount,
        CancellationToken cancellationToken)
    {
        var rows = new List<(long RowId, short Stack, short Quality)>();
        await using (var command = new NpgsqlCommand("""
            SELECT id, stack, item_quality
            FROM character_items
            WHERE user_id = @characterId
              AND item_location = @kitBagLocation
              AND prop_id = @itemId
              AND stack > 0
              AND (@bagSlot IS NULL OR slot_index = @bagSlot)
            ORDER BY slot_index
            FOR UPDATE;
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("characterId", characterId);
            command.Parameters.AddWithValue("kitBagLocation", ItemLocationKitBag);
            command.Parameters.AddWithValue("itemId", itemId);
            command.Parameters.AddWithValue(
                "bagSlot",
                bagSlot is { } slot ? slot : DBNull.Value);
            await using var reader =
                await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                rows.Add((
                    reader.GetInt64(0),
                    reader.GetInt16(1),
                    reader.GetInt16(2)));
            }
        }

        if (bagSlot is not null && rows.Count == 0)
        {
            // The item control held something other than this egg, or nothing at
            // all, so there is nothing to donate.
            return new FarmEggConsumption(FarmEggRefusal.NoEggInBox, 0, 0);
        }

        var held = rows.Sum(static row => (long)row.Stack);
        if (held < eggCount)
        {
            return new FarmEggConsumption(FarmEggRefusal.NotEnoughEggs, 0, 0);
        }

        var draws = new List<(long RowId, short Left, int Taken, int Points)>();
        var remaining = eggCount;
        var points = 0;
        foreach (var row in rows)
        {
            if (remaining == 0)
            {
                break;
            }

            if (!LelantineFarmPointsPolicy.TryResolveEggDonationPoints(
                    row.Quality,
                    out var award))
            {
                return new FarmEggConsumption(
                    FarmEggRefusal.UnsupportedAptitude,
                    0,
                    0);
            }

            var taken = Math.Min(remaining, row.Stack);
            remaining -= taken;
            points = checked(points + (award * taken));
            draws.Add((
                row.RowId,
                checked((short)(row.Stack - taken)),
                taken,
                award));
        }

        foreach (var draw in draws)
        {
            await using var command = draw.Left == 0
                ? new NpgsqlCommand("""
                    DELETE FROM character_items WHERE id = @rowId;
                    """, connection, transaction)
                : new NpgsqlCommand("""
                    UPDATE character_items
                    SET stack = @stack,
                        updated_at = now()
                    WHERE id = @rowId;
                    """, connection, transaction);
            command.Parameters.AddWithValue("rowId", draw.RowId);
            if (draw.Left != 0)
            {
                command.Parameters.AddWithValue("stack", draw.Left);
            }

            var written = await command.ExecuteNonQueryAsync(cancellationToken);
            if (written != 1)
            {
                throw new InvalidOperationException(
                    $"Kit-bag row {draw.RowId} changed while consuming farm eggs.");
            }
        }

        return new FarmEggConsumption(
            FarmEggRefusal.None,
            draws.Sum(static draw => draw.Taken),
            points);
    }

    /// <summary>Why a run of the egg consumption wrote nothing.</summary>
    private enum FarmEggRefusal
    {
        /// <summary>Nothing was refused; the requested eggs were consumed.</summary>
        None,

        /// <summary>The bag holds fewer eggs than the request asked for.</summary>
        NotEnoughEggs,

        /// <summary>
        /// The submitted item control held no egg of the requested item.
        /// </summary>
        NoEggInBox,

        /// <summary>
        /// An egg the request would take carries an aptitude the activity does
        /// not score.
        /// </summary>
        UnsupportedAptitude
    }

    /// <summary>What one run of the egg consumption actually took and earned.</summary>
    private readonly record struct FarmEggConsumption(
        FarmEggRefusal Refusal,
        int Eggs,
        int Points);

    public async Task CreditFarmKillAsync(
        int characterId,
        byte faction,
        int points,
        CancellationToken cancellationToken = default)
    {
        if (points <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(points));
        }

        // Keyed by character and faction, so a character who changed camp keeps
        // the kills they earned for each camp in that camp's own row and the
        // faction total stays a true sum of same-faction scores.
        await using var connection =
            await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            INSERT INTO lelantine_farm_kill_points (
                character_id, faction, kill_points, credited_kills
            )
            VALUES (@characterId, @faction, @points, 1)
            ON CONFLICT (character_id, faction) DO UPDATE
            SET kill_points =
                    lelantine_farm_kill_points.kill_points
                    + EXCLUDED.kill_points,
                credited_kills =
                    lelantine_farm_kill_points.credited_kills + 1,
                updated_at = now();
            """, connection);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue("faction", (short)faction);
        command.Parameters.AddWithValue("points", (long)points);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<FarmScoreSnapshot?> ReadFarmScoresAsync(
        int characterId,
        byte faction,
        CancellationToken cancellationToken = default)
    {
        await using var connection =
            await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            FarmScoreReadSql, connection);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue("faction", (short)faction);
        command.Parameters.AddWithValue(
            "spartaFaction",
            (short)LelantineFarmPointsPolicy.SpartaFaction);
        command.Parameters.AddWithValue(
            "athensFaction",
            (short)LelantineFarmPointsPolicy.AthensFaction);

        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new FarmScoreSnapshot(
            faction,
            reader.GetInt64(0),
            reader.GetInt64(1),
            reader.GetInt64(2),
            reader.GetInt64(3),
            reader.GetInt64(4),
            reader.GetInt64(5),
            reader.GetInt64(6));
    }
}
