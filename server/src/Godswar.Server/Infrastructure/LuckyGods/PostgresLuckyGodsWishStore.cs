using Godswar.Server.Application.LuckyGods;
using Godswar.Server.Application.Realms;
using Npgsql;

namespace Godswar.Server.Infrastructure.LuckyGods;

/// <summary>
/// Reads and advances the divine wish's durable counters.
/// </summary>
/// <remarks>
/// One row per character
/// (<c>public.character_lucky_gods_wish</c>) carries the realm day the counters
/// belong to, so an older day reads as an untouched one and no scheduled reset is
/// needed. The claim is the only write that can pay experience: it locks the row
/// with <c>FOR UPDATE</c>, reads the pool, zeroes it and commits, so two claims
/// racing for one prize cannot both be paid.
/// </remarks>
internal sealed class PostgresLuckyGodsWishStore : ILuckyGodsWishStore
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgresLuckyGodsWishStore(NpgsqlDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        _dataSource = dataSource;
    }

    public async Task<LuckyGodsWishState> ReadAsync(
        int characterId,
        RealmCalendar calendar,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(calendar);
        var day = calendar.GetDay(now);
        await using var command = _dataSource.CreateCommand(
            """
            SELECT usage_date, used_count, last_used_at, streak, pending_experience
            FROM public.character_lucky_gods_wish
            WHERE character_id = @characterId;
            """);
        command.Parameters.AddWithValue("characterId", characterId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return new LuckyGodsWishState(day, 0, null, 0, 0);
        }

        var storedDay = reader.GetFieldValue<DateOnly>(0);
        if (storedDay != day)
        {
            // A row from an earlier realm day is a fresh day: the count, the wait,
            // the streak and yesterday's unclaimed prize are all void, which is the
            // client's own "第二天可就不算数啦".
            return new LuckyGodsWishState(day, 0, null, 0, 0);
        }

        return new LuckyGodsWishState(
            day,
            reader.GetInt32(1),
            reader.IsDBNull(2)
                ? (DateTimeOffset?)null
                : reader.GetFieldValue<DateTimeOffset>(2),
            reader.GetInt32(3),
            reader.GetInt32(4));
    }

    /// <summary>
    /// Books one made wish: the day's count, the moment, and where the streak and
    /// pool stand after it.
    /// </summary>
    public async Task RecordGuessAsync(
        int characterId,
        RealmCalendar calendar,
        DateTimeOffset now,
        int streak,
        int pendingExperience,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(calendar);
        var day = calendar.GetDay(now);
        await using var command = _dataSource.CreateCommand(
            """
            INSERT INTO public.character_lucky_gods_wish (
                character_id,
                usage_date,
                used_count,
                last_used_at,
                streak,
                pending_experience,
                updated_at
            )
            VALUES (
                @characterId,
                @usageDate,
                1,
                @now,
                @streak,
                @pendingExperience,
                @now
            )
            ON CONFLICT (character_id) DO UPDATE
            SET usage_date = EXCLUDED.usage_date,
                used_count = CASE
                    WHEN public.character_lucky_gods_wish.usage_date =
                        EXCLUDED.usage_date
                    THEN public.character_lucky_gods_wish.used_count + 1
                    ELSE 1
                END,
                last_used_at = EXCLUDED.last_used_at,
                streak = EXCLUDED.streak,
                pending_experience = EXCLUDED.pending_experience,
                updated_at = EXCLUDED.updated_at;
            """);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue("usageDate", day);
        command.Parameters.AddWithValue("now", now);
        command.Parameters.AddWithValue("streak", streak);
        command.Parameters.AddWithValue("pendingExperience", pendingExperience);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidOperationException(
                "The divine wish state row was not written.");
        }
    }

    /// <summary>
    /// Takes the unclaimed pool and returns what it held, zeroing both the pool and
    /// the streak. Returns zero when nothing was pending on today's row.
    /// </summary>
    /// <remarks>
    /// One statement, because the pool has to be read as it was <b>before</b> the
    /// zeroing: <c>UPDATE … RETURNING</c> reports the row after the set list, which
    /// is always zero, and a separate reader/second command on one connection is how
    /// this crashed a session (<c>A command is already in progress</c>) whenever the
    /// pool turned out to be empty. The <c>picked</c> CTE locks and reads the row,
    /// <c>taken</c> performs the zeroing, and the final select hands back the value
    /// <c>picked</c> saw, so the whole take is one round trip and one snapshot.
    /// </remarks>
    public async Task<int> ClaimAsync(
        int characterId,
        RealmCalendar calendar,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(calendar);
        var day = calendar.GetDay(now);
        await using var command = _dataSource.CreateCommand(
            """
            WITH picked AS (
                SELECT character_id, pending_experience
                FROM public.character_lucky_gods_wish
                WHERE character_id = @characterId
                    AND usage_date = @usageDate
                    AND pending_experience > 0
                FOR UPDATE
            ),
            taken AS (
                UPDATE public.character_lucky_gods_wish w
                SET pending_experience = 0,
                    streak = 0,
                    updated_at = @now
                FROM picked
                WHERE w.character_id = picked.character_id
                RETURNING w.character_id
            )
            SELECT pending_experience
            FROM picked
            WHERE EXISTS (SELECT 1 FROM taken);
            """);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue("usageDate", day);
        command.Parameters.AddWithValue("now", now);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var pending = await reader.ReadAsync(cancellationToken) ? reader.GetInt32(0) : 0;
        Console.WriteLine(
            $"[lucky-gods] claim took character={characterId} day={day:yyyy-MM-dd} " +
            $"pool={pending}");
        return pending;
    }
}
