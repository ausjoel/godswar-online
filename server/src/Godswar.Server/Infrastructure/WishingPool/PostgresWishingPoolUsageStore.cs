using Godswar.Server.Application.Realms;
using Npgsql;

namespace Godswar.Server.Infrastructure.WishingPool;

/// <summary>
/// One character's Wishing Pool free-wish usage for the realm's current day.
/// </summary>
internal readonly record struct WishingPoolUsage(
    DateOnly Day,
    int UsedCount,
    DateTimeOffset? LastUsedAt)
{
    /// <summary>
    /// The client allows three free wishes per day.
    /// </summary>
    public const int DailyLimit = 3;

    /// <summary>
    /// The client allows one free wish per hour.
    /// </summary>
    public static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    // ROUND2: cap.wishing_pool_daily (stock 3).
    public int Remaining => Math.Max(0, (int)Application.Rates.ServerRates.Current.Absolute(
        Application.Rates.ServerRateKeys.WishingPoolDaily) - UsedCount);
}

/// <summary>
/// Reads and advances the durable free-wish counter.
/// </summary>
/// <remarks>
/// The counter lives in one row per character
/// (<c>public.character_wishing_pool_usage</c>) and carries the realm day it
/// belongs to. A row from an earlier day is reported as an untouched day, so the
/// daily reset needs no scheduled work and a single read answers the whole check.
/// </remarks>
internal sealed class PostgresWishingPoolUsageStore
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgresWishingPoolUsageStore(NpgsqlDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        _dataSource = dataSource;
    }

    public async Task<WishingPoolUsage> ReadAsync(
        int characterId,
        RealmCalendar calendar,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(calendar);
        var day = calendar.GetDay(now);
        await using var command = _dataSource.CreateCommand(
            """
            SELECT usage_date, used_count, last_used_at
            FROM public.character_wishing_pool_usage
            WHERE character_id = @characterId;
            """);
        command.Parameters.AddWithValue("characterId", characterId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return new WishingPoolUsage(day, 0, null);
        }

        var storedDay = reader.GetFieldValue<DateOnly>(0);
        var usedCount = reader.GetInt32(1);
        var lastUsedAt = reader.IsDBNull(2)
            ? (DateTimeOffset?)null
            : reader.GetFieldValue<DateTimeOffset>(2);
        // A row written on an earlier realm day is a fresh day: the count and the
        // interval both start over.
        return storedDay == day
            ? new WishingPoolUsage(day, usedCount, lastUsedAt)
            : new WishingPoolUsage(day, 0, null);
    }

    public async Task RecordAsync(
        int characterId,
        RealmCalendar calendar,
        DateTimeOffset now,
        int skillBookItemId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(calendar);
        var day = calendar.GetDay(now);
        await using var command = _dataSource.CreateCommand(
            """
            INSERT INTO public.character_wishing_pool_usage (
                character_id,
                usage_date,
                used_count,
                last_used_at,
                last_skill_book_item_id,
                updated_at
            )
            VALUES (
                @characterId,
                @usageDate,
                1,
                @now,
                @skillBookItemId,
                @now
            )
            ON CONFLICT (character_id) DO UPDATE
            SET usage_date = EXCLUDED.usage_date,
                used_count = CASE
                    WHEN public.character_wishing_pool_usage.usage_date =
                        EXCLUDED.usage_date
                    THEN public.character_wishing_pool_usage.used_count + 1
                    ELSE 1
                END,
                last_used_at = EXCLUDED.last_used_at,
                last_skill_book_item_id = EXCLUDED.last_skill_book_item_id,
                updated_at = EXCLUDED.updated_at;
            """);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue("usageDate", day);
        command.Parameters.AddWithValue("now", now);
        command.Parameters.AddWithValue("skillBookItemId", skillBookItemId);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidOperationException(
                "The wishing pool usage row was not written.");
        }
    }
}
