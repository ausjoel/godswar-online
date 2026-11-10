using Godswar.Server.Application.WorldInstances;

namespace Godswar.Server.ProtocolChecks;

// Lelantine Farm round clock and reward formulas (wiki page): Saturday 22:45-23:30 Manila, winner pet exp
// 140,000 + 5,000 x level, character exp 10,000 + 50 x level^2, talent 16 + 0.2 x level, half for the loser, full for a draw.
internal static class LelantineFarmRoundChecks
{
    public const string CheckName = "Lelantine Farm round clock and rewards";

    // 2026-11-07 is a Saturday; 22:45 Manila (+8) is 14:45 UTC.
    private static DateTimeOffset Utc(int day, int hour, int minute, int second = 0) =>
        new(2026, 11, day, hour, minute, second, TimeSpan.Zero);

    public static Task RunAsync()
    {
        // FINALISE2 (the admin F-03/F-04): the round runs on the realm clock. The Manila assertions below pin a Manila realm;
        // the AEST block after them is the live realm (Australia/Brisbane).
        Godswar.Server.Application.Gates.RealmClock.Calendar =
            Godswar.Server.Application.Realms.RealmCalendar.CreateForTesting(Godswar.Server.Domain.World.Instances.RealmId.Tempest, "Asia/Manila");
        try
        {
            RunManila();
            Godswar.Server.Application.Gates.RealmClock.Calendar =
                Godswar.Server.Application.Realms.RealmCalendar.CreateForTesting(Godswar.Server.Domain.World.Instances.RealmId.Tempest, "Australia/Brisbane");
            // Saturday 22:45 AEST = 12:45 UTC.
            var (aestStart, aestEnd) = LelantineFarmRoundPolicy.LatestWindow(Utc(7, 13, 0));
            Check.Equal(Utc(7, 12, 45), aestStart, "AEST: the Saturday round starts 22:45 AEST (12:45 UTC)");
            Check.Equal(Utc(7, 13, 30), aestEnd, "AEST: and ends 23:30 AEST");
            Check.True(!LelantineFarmRoundPolicy.IsOpen(Utc(7, 14, 50)), "AEST: the old Manila window (00:50 AEST Sunday) is closed");
        }
        finally
        {
            Godswar.Server.Application.Gates.RealmClock.Calendar = null;
        }

        return Task.CompletedTask;
    }

    private static void RunManila()
    {
        var (start, end) = LelantineFarmRoundPolicy.LatestWindow(Utc(7, 15, 0));
        Check.Equal(Utc(7, 14, 45), start, "the Saturday window starts 22:45 Manila");
        Check.Equal(Utc(7, 15, 30), end, "and ends 23:30 Manila");
        Check.True(!LelantineFarmRoundPolicy.IsOpen(Utc(7, 14, 44, 59)), "closed one second before");
        Check.True(LelantineFarmRoundPolicy.IsOpen(Utc(7, 14, 45)), "open at 22:45");
        Check.True(LelantineFarmRoundPolicy.IsOpen(Utc(7, 15, 29, 59)), "open until 23:30");
        Check.True(!LelantineFarmRoundPolicy.IsOpen(Utc(7, 15, 30)), "closed at 23:30");
        Check.True(!LelantineFarmRoundPolicy.IsOpen(Utc(8, 14, 45)), "closed on Sunday");
        Check.Equal(Utc(7, 14, 45), LelantineFarmRoundPolicy.LatestWindow(Utc(10, 3, 0)).Start, "midweek still points at last Saturday");
        Check.Equal(Utc(7, 14, 45), LelantineFarmRoundPolicy.LatestWindow(Utc(14, 14, 44)).Start, "the next Saturday before 22:45 still points at the last one");
        Check.Equal(Utc(14, 14, 45), LelantineFarmRoundPolicy.LatestWindow(Utc(14, 14, 45)).Start, "and flips at 22:45");
        // Manila is already Sunday when UTC is still Saturday evening: a Saturday 22:45 Manila round is Saturday 14:45 UTC.
        Check.True(LelantineFarmRoundPolicy.IsOpen(new DateTimeOffset(2026, 11, 7, 22, 50, 0, TimeSpan.FromHours(8))), "Manila wall clock 22:50 is open");

        Check.True(LelantineFarmRoundPolicy.Winner(5, 3) == 0 && LelantineFarmRoundPolicy.Winner(3, 5) == 1 && LelantineFarmRoundPolicy.Winner(4, 4) is null, "winner");
        Check.True(LelantineFarmRoundPolicy.OutcomeFor(0, 5, 3) == FarmRoundOutcome.Won && LelantineFarmRoundPolicy.OutcomeFor(1, 5, 3) == FarmRoundOutcome.Lost
            && LelantineFarmRoundPolicy.OutcomeFor(1, 4, 4) == FarmRoundOutcome.Draw, "outcomes");
        var win = LelantineFarmRoundPolicy.RewardFor(100, FarmRoundOutcome.Won);
        Check.True(win == new FarmRoundReward(640_000, 510_000, 36), $"winner level 100: {win}");
        var lose = LelantineFarmRoundPolicy.RewardFor(100, FarmRoundOutcome.Lost);
        Check.True(lose == new FarmRoundReward(320_000, 255_000, 18), $"loser gets half: {lose}");
        Check.True(LelantineFarmRoundPolicy.RewardFor(100, FarmRoundOutcome.Draw) == win, "a draw pays both factions the full winner reward");
    }
}
