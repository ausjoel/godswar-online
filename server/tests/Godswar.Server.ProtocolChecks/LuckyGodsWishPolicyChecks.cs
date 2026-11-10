using Godswar.Server.Application.LuckyGods;

namespace Godswar.Server.ProtocolChecks;

internal static class LuckyGodsWishPolicyChecks
{
    public const string CheckName =
        "Divine wish ladder, encodings and client-stated limits stay inside what the client can print";

    public static Task RunAsync()
    {
        Check.True(
            Enumerable.Range(0, 8).Select(LuckyGodsWishPolicy.PoolAfter).SequenceEqual(
                [0, 210_000, 420_000, 840_000, 1_680_000, 3_360_000, 6_720_000, 13_490_000]),
            "the pool doubles per consecutive correct guess and lands on the ceiling at seven");

        Check.True(
            Enumerable.Range(1, LuckyGodsWishPolicy.MaximumStreak)
                .All(streak =>
                    LuckyGodsWishPolicy.PoolAfter(streak) >
                    LuckyGodsWishPolicy.PoolAfter(streak - 1)),
            "a longer streak is never worth less than a shorter one");

        // The client reads each line back as (SubID - tail) / 10, so an encoding has
        // to survive that exact inverse.
        Check.Equal(
            2_100_009,
            LuckyGodsWishPolicy.ContinuedSubId(210_000),
            "the first correct guess carries its pool above the tail-9 digit");
        Check.Equal(
            134_900_002,
            LuckyGodsWishPolicy.CompletedSubId(13_490_000),
            "the seventh correct guess carries the ceiling above the tail-2 digit");
        Check.Equal(
            108,
            LuckyGodsWishPolicy.MissedSubId(10),
            "a miss reports the wishes left today above the tail-8 digit");
        Check.Equal(
            56,
            LuckyGodsWishPolicy.WaitingSubId(5),
            "the wait reports whole minutes above the tail-6 digit");

        Check.True(
            LuckyGodsWishPolicy.ContinuedSubId(
                LuckyGodsWishPolicy.CeilingExperience) <= int.MaxValue &&
            LuckyGodsWishPolicy.CompletedSubId(
                LuckyGodsWishPolicy.CeilingExperience) <= int.MaxValue,
            "the full-streak pool still fits the 32-bit SubID field the client reads");

        Check.Equal(
            LuckyGodsWishPolicy.CeilingExperience,
            (LuckyGodsWishPolicy.CompletedSubId(13_490_000) - 2) / 10,
            "the seventh line prints the pool it was encoded from");

        Check.Equal(
            8,
            LuckyGodsWishPolicy.MissedSubId(0),
            "an exhausted day is printed as zero wishes rather than an invented line");

        Check.Equal(
            10,
            LuckyGodsWishPolicy.DailyLimit,
            "NF_L0_L001 says ten wishes a day, so the allowance is not a server-side guess");
        Check.Equal(
            TimeSpan.FromMinutes(5),
            LuckyGodsWishPolicy.Interval,
            "NF_L0_L001 says one wish every five minutes");
        Check.Equal(
            55,
            LuckyGodsWishPolicy.MinimumLevel,
            "NF_L0_L012 gates the wish below level 55");
        Check.Equal(
            7,
            LuckyGodsWishPolicy.MaximumStreak,
            "NF_L0_L022 is the client's seventh consecutive correct guess, so seven ends the round");

        // The hit rate is a server-side economy choice, so the only thing worth
        // pinning is that the drawn roll is read the way it is documented: a roll of
        // Random.Shared.Next(100) hits for exactly the advertised share of its range.
        Check.Equal(
            LuckyGodsWishPolicy.HitChancePercent,
            Enumerable.Range(0, 100).Count(LuckyGodsWishPolicy.IsHit),
            "one draw of a hundred values accepts the advertised percentage of wishes");
        Check.True(
            LuckyGodsWishPolicy.IsHit(LuckyGodsWishPolicy.HitChancePercent - 1) &&
            !LuckyGodsWishPolicy.IsHit(LuckyGodsWishPolicy.HitChancePercent),
            "the accepted range ends where the chance says it does");

        // A reply has to be clickable in the way the script actually draws it: the
        // win frame carries the win text and all three buttons, while a result frame
        // carries nothing but itself (a result in the same frame as buttons makes the
        // client close the window).
        Check.True(
            LuckyGodsWishPolicy.ReplyFor(1, 210_000, 9)
                .SequenceEqual([2_100_009, 104, 105]),
            "a win answers the pool line plus the Apollo and claim buttons");
        Check.True(
            LuckyGodsWishPolicy.ReplyFor(7, 13_490_000, 3)
                .SequenceEqual([134_900_002]),
            "the seventh win is a result frame on its own");
        Check.True(
            LuckyGodsWishPolicy.ReplyFor(0, 0, 9).SequenceEqual([98]),
            "a miss is a result frame on its own");
        Check.True(
            LuckyGodsWishPolicy.ReplyFor(1, 210_000, 9)
                .SkipLast(1)
                .All(LuckyGodsWishPolicy.IsGuessClick) &&
            LuckyGodsWishPolicy.IsClaimClick(
                LuckyGodsWishPolicy.ReplyFor(1, 210_000, 9)[2]),
            "every button the win frame draws routes back to its own action");
        Check.True(
            !new[] { 98, 108, 134_900_002, 56 }
                .Any(LuckyGodsWishPolicy.IsGuessClick) &&
            !new[] { 98, 108, 134_900_002, 56 }
                .Any(LuckyGodsWishPolicy.IsClaimClick),
            "a result number is never mistaken for a clickable button");
        Check.True(
            !new[] { 100, 101, 104, 105, 1000, 2_100_009 }
                .Any(subId => LuckyGodsWishPolicy.IsGuessClick(subId) &&
                    LuckyGodsWishPolicy.IsClaimClick(subId)),
            "no button answers to both the guess and the claim route");
        Check.True(
            new[] { 100, 101 }.All(LuckyGodsWishPolicy.IsGuessClick) &&
            LuckyGodsWishPolicy.IsClaimClick(1000),
            "the first page's three buttons classify the same as the later frames");

        // A wrong guess costs the whole pot: the ladder only ever pays from what the
        // current streak is worth, so a streak of zero has nothing in it. The claim
        // button and the realm's midnight are the other two ways out.
        Check.Equal(
            0,
            LuckyGodsWishPolicy.PoolAfter(0),
            "a lost guess leaves an empty pot behind");
        Check.Equal(
            210_000,
            LuckyGodsWishPolicy.PoolAfter(1),
            "the first win of a fresh ladder pays the base again");

        // A paid claim answers with the script's own "take the prize and stop" line,
        // never with the empty-pool line the player just proved wrong.
        Check.Equal(
            37,
            LuckyGodsWishPolicy.ClaimedSubId(3),
            "the claim line carries the wishes left today above the tail-7 digit");
        Check.Equal(
            3,
            (LuckyGodsWishPolicy.ClaimedSubId(3) - 7) / 10,
            "the claim line prints the wishes the client is told it has left");
        Check.True(
            !LuckyGodsWishPolicy.IsGuessClick(LuckyGodsWishPolicy.ClaimedSubId(3)) &&
            !LuckyGodsWishPolicy.IsClaimClick(LuckyGodsWishPolicy.ClaimedSubId(3)),
            "the claim frame is a result, not a button the player could press again");

        Check.Throws<ArgumentOutOfRangeException>(
            () => LuckyGodsWishPolicy.ContinuedSubId(-1),
            "a negative value cannot be encoded into a SubID");
        Check.Throws<OverflowException>(
            () => LuckyGodsWishPolicy.ContinuedSubId(300_000_000),
            "an unprintable amount fails loudly instead of wrapping into a smaller number");

        return Task.CompletedTask;
    }
}
