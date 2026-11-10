using Godswar.Server.Application.Realms;

namespace Godswar.Server.Application.LuckyGods;

/// <summary>
/// One character's divine-wish state for the realm's current day.
/// </summary>
internal readonly record struct LuckyGodsWishState(
    DateOnly Day,
    int UsedCount,
    DateTimeOffset? LastUsedAt,
    int Streak,
    int PendingExperience)
{
    public int Remaining =>
        // ROUND2: cap.lucky_gods_daily (stock 10).
        Math.Max(0, (int)Rates.ServerRates.Current.Absolute(
            Rates.ServerRateKeys.LuckyGodsDaily) - UsedCount);
}

/// <summary>
/// The divine wish's durable counters: today's allowance, the five-minute wait, the
/// streak, and the prize pool the player has won but not claimed.
/// </summary>
/// <remarks>
/// The claim is the only write that can pay experience. It reads the pool and zeroes
/// it inside one locked transaction, so two claims racing for the same prize cannot
/// both be paid: the second reads a pool of zero.
/// </remarks>
internal interface ILuckyGodsWishStore
{
    Task<LuckyGodsWishState> ReadAsync(
        int characterId,
        RealmCalendar calendar,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    Task RecordGuessAsync(
        int characterId,
        RealmCalendar calendar,
        DateTimeOffset now,
        int streak,
        int pendingExperience,
        CancellationToken cancellationToken);

    Task<int> ClaimAsync(
        int characterId,
        RealmCalendar calendar,
        DateTimeOffset now,
        CancellationToken cancellationToken);
}
