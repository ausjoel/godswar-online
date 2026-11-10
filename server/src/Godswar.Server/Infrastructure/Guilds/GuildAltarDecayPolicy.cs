using Godswar.Server.Application.Guilds;

namespace Godswar.Server.Infrastructure.Guilds;

/// <summary>
/// The altar's hourly drain: how many offering points an altar should still hold
/// after a stretch of time.
/// </summary>
/// <remarks>
/// <para>
/// <b>The four rates are the user's own specification (2026-09-23), not the
/// client's <c>BuildingConsume</c> table.</b> <c>Consortia.xml</c> ships a
/// six-row <c>BuildingConsume</c> table but never says what it means, and its rows
/// do not line up with <c>NF_L0_GH87</c>'s two spoken bands either; reading it as
/// the drain rate was an unproven inference. The rates below replace it, and this
/// class no longer consults the database:
/// </para>
/// <list type="table">
/// <item><description>over 800000 → 8000 an hour</description></item>
/// <item><description>600000 to 800000 → 5000 an hour</description></item>
/// <item><description>300000 to 600000 → 1500 an hour</description></item>
/// <item><description>up to 300000 → 600 an hour</description></item>
/// </list>
/// <para>
/// The bands are inclusive of their lower bound ("大于30万" is 1500, so 300000
/// itself pays 1500), and the balance is never read below zero.
/// </para>
/// <para>
/// The drain is applied an hour at a time, with the band re-read from the balance
/// as it stands at that hour. A balance therefore never pays a rate it has already
/// fallen out of, and one that cannot pay an hour's rate goes to zero rather than
/// negative.
/// </para>
/// </remarks>
internal static class GuildAltarDecayPolicy
{
    /// <summary>Balance above which the top rate applies.</summary>
    public const long TopBandFloor = 800_000;

    /// <summary>Balance above which the second rate applies.</summary>
    public const long SecondBandFloor = 600_000;

    /// <summary>Balance above which the third rate applies.</summary>
    public const long ThirdBandFloor = 300_000;

    /// <summary>Hourly drain for a balance above <see cref="TopBandFloor"/>.</summary>
    public const long TopRate = 8_000;

    /// <summary>Hourly drain for the 600000-800000 band.</summary>
    public const long SecondRate = 5_000;

    /// <summary>Hourly drain for the 300000-600000 band.</summary>
    public const long ThirdRate = 1_500;

    /// <summary>Hourly drain at or below <see cref="ThirdBandFloor"/>.</summary>
    public const long BottomRate = 600;

    /// <summary>
    /// The drain a balance of this size pays each hour.
    /// </summary>
    public static long HourlyRate(long points) => points switch
    {
        > TopBandFloor => TopRate,
        > SecondBandFloor => SecondRate,
        > ThirdBandFloor => ThirdRate,
        _ => BottomRate
    };

    /// <summary>
    /// What a balance has left after every whole hour it was owed since
    /// <paramref name="settledAt"/>.
    /// </summary>
    /// <returns>
    /// The remaining points and the moment the drain was applied up to. The
    /// timestamp only advances by whole hours, so the part of an hour that has not
    /// been charged yet is not lost.
    /// </returns>
    public static (long Points, DateTimeOffset SettledAt) Settle(
        long points,
        DateTimeOffset settledAt,
        DateTimeOffset now)
    {
        if (points <= 0)
        {
            // An altar that holds nothing owes nothing, and there is no hour to
            // mark: advancing the timestamp here would rewrite the row on every
            // read. It stays where it is until points are offered again.
            return (points, settledAt);
        }

        var hours = (long)Math.Floor((now - settledAt).TotalHours);
        if (hours <= 0)
        {
            return (points, settledAt);
        }

        for (var hour = 0L; hour < hours; hour++)
        {
            if (points <= 0)
            {
                break;
            }

            points = Math.Max(0, points - HourlyRate(points));
        }

        if (points <= 0)
        {
            // The balance ran out part-way through. The mark goes to the moment it
            // did rather than to now, so the row is not rewritten on every later
            // read, and the next offering starts from an honest clock.
            return (0, Min(now, settledAt.AddHours(hours)));
        }

        return (points, settledAt.AddHours(hours));
    }

    private static DateTimeOffset Min(DateTimeOffset left, DateTimeOffset right) =>
        left <= right ? left : right;
}
