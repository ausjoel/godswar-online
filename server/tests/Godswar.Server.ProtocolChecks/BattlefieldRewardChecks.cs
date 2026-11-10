using Godswar.Server.Application.Rates;
using Godswar.Server.Application.Realms;
using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Domain.World.Instances;

namespace Godswar.Server.ProtocolChecks;

// BATTLEFIELD: Pindus / Ni Mini round windows, IGG reward formulas (guide acid=123 and acid=182), the Awarder reply
// numbers the client's NpcFunWar.lua draws, the claim deadline, and the new rates' defaults.
internal static class BattlefieldRewardChecks
{
    public const string CheckName = "Battlefield rounds, rewards and Awarder replies";

    private static DateTimeOffset Utc(int day, int hour, int minute) => new(2026, 11, day, hour, minute, 0, TimeSpan.Zero);

    public static Task RunAsync()
    {
        ServerRates.ResetToDefaults();
        try
        {
            CheckDefaults();
            CheckRounds();
            CheckRewards();
            CheckReplies();
            CheckPindusBand();
            CheckPaidAmounts();
        }
        finally
        {
            ServerRates.ResetToDefaults();
        }
        return Task.CompletedTask;
    }

    private static void CheckDefaults()
    {
        var rates = ServerRates.Current;
        Check.Equal(0L, rates.Absolute(ServerRateKeys.BattlefieldAwarder), "RELEASE: the Awarder is off by default");
        Check.Equal(0L, rates.Absolute(ServerRateKeys.BattlefieldScoring), "RELEASE: scoring is off by default");
        Check.Equal(0L, rates.Absolute(ServerRateKeys.BattlefieldClaimHours), "claim on the battle day by default (IGG)");
        Check.Equal(7L, rates.Absolute(ServerRateKeys.BattlefieldTitleDays), "titles last a week by default");
        Check.Equal(5L, rates.Absolute(ServerRateKeys.BattlefieldPindusKillPoints), "a Pindus kill is 5 points (IGG)");
        Check.Equal(800, rates.Multiply(ServerRateKeys.BattlefieldReputation, 800), "reputation x1 by default");
        Check.True(ServerRateCatalog.Definitions.Count(d => d.Category == ServerRateCatalog.BattlefieldCategory) == 10,
            "ten battlefield settings, in their own category");
        Check.Equal(0L, rates.Absolute(ServerRateKeys.BattlefieldNiMiniIggTimes), "RELEASE: Ni Mini at the stock times by default");
        // BATTLEFIELD2
        Check.Equal(1L, rates.Absolute(ServerRateKeys.BattlefieldPindusIggTimes), "Pindus at the IGG times by default");
        Check.Equal(1L, rates.Absolute(ServerRateKeys.BattlefieldPindusBand), "Pindus level 60-200 by default");
        Check.Equal(0L, rates.Absolute(ServerRateKeys.BattlefieldShowPaidAmounts), "RELEASE: the Awarder shows the base amounts by default");
    }

    private static void CheckRounds()
    {
        var calendar = RealmCalendar.CreateForTesting(new RealmId(1));
        // 2026-11-04 is a Wednesday: IGG Pindus window 11:00-11:45 (BATTLEFIELD2; realm time; this calendar is UTC).
        var round = BattlefieldRewardPolicy.CurrentRound(BattlefieldRewardPolicy.Pindus, calendar, Utc(4, 11, 10));
        Check.True(round == (Utc(4, 11, 0), Utc(4, 11, 45)), $"Wednesday Pindus round 11:00-11:45 (IGG): {round}");
        Check.True(BattlefieldRewardPolicy.CurrentRound(BattlefieldRewardPolicy.Pindus, calendar, Utc(4, 11, 45)) is null &&
                   BattlefieldRewardPolicy.CurrentRound(BattlefieldRewardPolicy.Pindus, calendar, Utc(4, 21, 10)) is null,
            "closed at 11:45 and at the stock 21:00 start");
        var saturday = BattlefieldRewardPolicy.CurrentRound(BattlefieldRewardPolicy.Pindus, calendar, Utc(7, 21, 44));
        var sundayPindus = BattlefieldRewardPolicy.CurrentRound(BattlefieldRewardPolicy.Pindus, calendar, Utc(8, 11, 0));
        Check.True(saturday == (Utc(7, 21, 0), Utc(7, 21, 45)) && sundayPindus == (Utc(8, 11, 0), Utc(8, 11, 45)) &&
                   BattlefieldRewardPolicy.CurrentRound(BattlefieldRewardPolicy.Pindus, calendar, Utc(7, 8, 10)) is null &&
                   BattlefieldRewardPolicy.CurrentRound(BattlefieldRewardPolicy.Pindus, calendar, Utc(6, 21, 10)) is null,
            $"Saturday 21:00 and Sunday 11:00 Pindus rounds (IGG), no stock Friday/Saturday-morning round: {saturday} / {sundayPindus}");
        ServerRates.Install(ServerRatesSnapshot.Create(
            [KeyValuePair.Create(ServerRateKeys.BattlefieldPindusIggTimes, 0m)], 1, DateTimeOffset.UtcNow, "check", "test"));
        var stockPindus = BattlefieldRewardPolicy.CurrentRound(BattlefieldRewardPolicy.Pindus, calendar, Utc(4, 21, 10));
        Check.True(stockPindus == (Utc(4, 21, 0), Utc(4, 21, 45)) &&
                   BattlefieldRewardPolicy.CurrentRound(BattlefieldRewardPolicy.Pindus, calendar, Utc(4, 11, 10)) is null,
            $"pindus_igg_times = 0: the stock Wednesday 21:00 round: {stockPindus}");
        ServerRates.ResetToDefaults();
        Check.True(BattlefieldRewardPolicy.CurrentRound(BattlefieldRewardPolicy.NiMini, calendar, Utc(4, 21, 10)) is null,
            "Ni Mini is closed on Wednesday");
        // 2026-11-06 is a Friday: Ni Mini 21:45-22:30 (IGG); 2026-11-08 a Sunday: 19:00-19:45; Saturday closed.
        // RELEASE: nimini_igg_times defaults to 0 now, so the IGG part turns it on.
        ServerRates.Install(ServerRatesSnapshot.Create(
            [KeyValuePair.Create(ServerRateKeys.BattlefieldNiMiniIggTimes, 1m)], 1, DateTimeOffset.UtcNow, "check", "test"));
        var ni = BattlefieldRewardPolicy.CurrentRound(BattlefieldRewardPolicy.NiMini, calendar, Utc(6, 22, 29));
        Check.True(ni == (Utc(6, 21, 45), Utc(6, 22, 30)), $"Friday Ni Mini round (IGG): {ni}");
        var sunday = BattlefieldRewardPolicy.CurrentRound(BattlefieldRewardPolicy.NiMini, calendar, Utc(8, 19, 0));
        Check.True(sunday == (Utc(8, 19, 0), Utc(8, 19, 45)), $"Sunday Ni Mini round (IGG): {sunday}");
        Check.True(BattlefieldRewardPolicy.CurrentRound(BattlefieldRewardPolicy.NiMini, calendar, Utc(6, 19, 10)) is null &&
                   BattlefieldRewardPolicy.CurrentRound(BattlefieldRewardPolicy.NiMini, calendar, Utc(7, 7, 10)) is null,
            "the stock Friday 19:00 and Saturday 07:00 windows are closed at the IGG times");
        ServerRates.Install(ServerRatesSnapshot.Create(
            [KeyValuePair.Create(ServerRateKeys.BattlefieldNiMiniIggTimes, 0m)], 1, DateTimeOffset.UtcNow, "check", "test"));
        var stock = BattlefieldRewardPolicy.CurrentRound(BattlefieldRewardPolicy.NiMini, calendar, Utc(6, 19, 44));
        Check.True(stock == (Utc(6, 19, 0), Utc(6, 19, 45)), $"nimini_igg_times = 0: the stock Friday 19:00 round: {stock}");
        ServerRates.ResetToDefaults();

        ServerRates.Install(ServerRatesSnapshot.Create(
            [KeyValuePair.Create(ServerRateKeys.PindusAlwaysOpen, 1m)], 1, DateTimeOffset.UtcNow, "check", "test"));
        var day = BattlefieldRewardPolicy.CurrentRound(BattlefieldRewardPolicy.Pindus, calendar, Utc(3, 10, 0));
        Check.True(day == (Utc(3, 0, 0), Utc(4, 0, 0)), $"always open: the round is the realm day: {day}");
        ServerRates.ResetToDefaults();

        // Review fix: a 1,440-minute window keeps ONE identity for its whole length (the old 720-minute scan made the
        // start slide every minute after 12 hours). Wednesday 11:00 + 1,440 minutes = Thursday 11:00 (IGG start).
        ServerRates.Install(ServerRatesSnapshot.Create(
            [KeyValuePair.Create(ServerRateKeys.BattlefieldWindowMinutes, 1440m)], 1, DateTimeOffset.UtcNow, "check", "test"));
        var early = BattlefieldRewardPolicy.CurrentRound(BattlefieldRewardPolicy.Pindus, calendar, Utc(4, 11, 5));
        var late = BattlefieldRewardPolicy.CurrentRound(BattlefieldRewardPolicy.Pindus, calendar, Utc(5, 10, 59));
        Check.True(early == (Utc(4, 11, 0), Utc(5, 11, 0)) && late == early,
            $"a 1,440-minute window is one round from start to end: {early} / {late}");
        ServerRates.ResetToDefaults();
        // A period open for longer than the scan (back-to-back windows) is anchored to its realm day, never sliding.
        var endless = BattlefieldRewardPolicy.CurrentRound(BattlefieldRewardPolicy.Pindus, calendar, Utc(4, 13, 7), _ => true);
        var endless2 = BattlefieldRewardPolicy.CurrentRound(BattlefieldRewardPolicy.Pindus, calendar, Utc(4, 18, 41), _ => true);
        Check.True(endless == (Utc(4, 0, 0), Utc(5, 0, 0)) && endless2 == endless,
            $"an over-long open period is anchored to the realm day: {endless} / {endless2}");
        Check.Equal(1, BattlefieldRewardPolicy.PersonalKillPoints,
            "a Pindus kill gives the killer 1 personal point (client text; faction gets the rate's 5)");

        Check.Equal((short)1, BattlefieldRewardPolicy.ForMap(38), "map 38 is Pindus");
        Check.Equal((short)2, BattlefieldRewardPolicy.ForMap(34), "map 34 is Ni Mini");
        Check.Equal((short)0, BattlefieldRewardPolicy.ForMap(42), "the farm map does not score here");

        var deadline = BattlefieldRewardPolicy.ClaimDeadline(calendar, Utc(4, 21, 0), Utc(4, 21, 45));
        Check.Equal(Utc(4, 23, 55), deadline, "claim until 23:55 of the battle day");
        ServerRates.Install(ServerRatesSnapshot.Create(
            [KeyValuePair.Create(ServerRateKeys.BattlefieldClaimHours, 48m)], 1, DateTimeOffset.UtcNow, "check", "test"));
        Check.Equal(Utc(6, 21, 45), BattlefieldRewardPolicy.ClaimDeadline(calendar, Utc(4, 21, 0), Utc(4, 21, 45)),
            "claim_hours counts from the end of the battle");
        ServerRates.ResetToDefaults();
    }

    private static void CheckRewards()
    {
        Check.True(BattlefieldRewardPolicy.OutcomeFor(1, 40, 75) == BattlefieldOutcome.Won &&
                   BattlefieldRewardPolicy.OutcomeFor(0, 40, 75) == BattlefieldOutcome.Lost &&
                   BattlefieldRewardPolicy.OutcomeFor(0, 9, 9) == BattlefieldOutcome.Draw, "outcomes");
        var won = BattlefieldRewardPolicy.PindusReward(100, BattlefieldOutcome.Won, 50, 300);
        Check.True(won == new BattlefieldReward(80_000, 100, 800, 300, 1_800), $"Pindus win at level 100: {won}");
        var lost = BattlefieldRewardPolicy.PindusReward(101, BattlefieldOutcome.Lost, 50, 300);
        Check.True(lost == new BattlefieldReward(40_400, 50, 400, 202, 1_212), $"Pindus loss at level 101: {lost}");
        var draw = BattlefieldRewardPolicy.PindusReward(100, BattlefieldOutcome.Draw, 50, 300);
        Check.True(draw == new BattlefieldReward(40_000, 50, 400, 150, 900), $"a draw is half the winner's: {draw}");
        Check.Equal(600, BattlefieldRewardPolicy.NiMiniReward(BattlefieldOutcome.Won).Reputation, "Ni Mini win 600 Rep");
        Check.Equal(200, BattlefieldRewardPolicy.NiMiniReward(BattlefieldOutcome.Lost).Reputation, "Ni Mini loss 200 Rep");
        Check.True(BattlefieldRewardPolicy.PindusKillTitle(1) == 5005 && BattlefieldRewardPolicy.PindusKillTitle(5) == 5006 &&
                   BattlefieldRewardPolicy.PindusKillTitle(6) == 0 && BattlefieldRewardPolicy.PindusTaskTitle(1) == 5007 &&
                   BattlefieldRewardPolicy.PindusTaskTitle(2) == 5008 && BattlefieldRewardPolicy.PindusTaskTitle(0) == 0,
            "Pindus title ladder");
        Check.True(BattlefieldRewardPolicy.HolyWaterPoints(false, 100, 100) == 30 &&
                   BattlefieldRewardPolicy.HolyWaterPoints(true, 100, 100) == 100 &&
                   BattlefieldRewardPolicy.HolyWaterPoints(false, 100, 130) == 30 &&
                   BattlefieldRewardPolicy.HolyWaterPoints(false, 100, 131) == 40 &&
                   BattlefieldRewardPolicy.HolyWaterPoints(true, 0, 5) == 120 &&
                   BattlefieldRewardPolicy.HolyWaterPoints(true, 131, 100) == 100,
            "Holy Water 30/100, 40/120 when the other side leads by more than 30%");
        Check.True(BattlefieldRewardPolicy.NiMiniTitle(1) == 5015 && BattlefieldRewardPolicy.NiMiniTitle(0) == 5016,
            "Ni Mini titles per faction");
    }

    private static void CheckReplies()
    {
        Check.True(BattlefieldAwarderReplies.PindusFactionReward(BattlefieldOutcome.Won, 120, 120, 96_000)
            .SequenceEqual([1963, 24_021]), "Pindus win line: 1963 + level x 200 + 21");
        Check.True(BattlefieldAwarderReplies.PindusFactionReward(BattlefieldOutcome.Lost, 120, 60, 48_000)
            .SequenceEqual([1964, 24_022]), "Pindus loss line: 1964 + level x 200 + 22");
        Check.True(BattlefieldAwarderReplies.PindusFactionReward(BattlefieldOutcome.Draw, 120, 60, 48_000)
            .SequenceEqual([1965, 1760, 60 * 200 + 29, 48_000 * 200 + 27]), "Pindus draw lines");
        Check.True(BattlefieldAwarderReplies.PindusPointsReward(120, 720).SequenceEqual([11762, 24_023, 144_024]),
            "Pindus points lines");
        Check.True(BattlefieldAwarderReplies.NiMiniReputationLine(BattlefieldOutcome.Won, 1) == 600 &&
                   BattlefieldAwarderReplies.NiMiniReputationLine(BattlefieldOutcome.Won, 0) == 601 &&
                   BattlefieldAwarderReplies.NiMiniReputationLine(BattlefieldOutcome.Lost, 1) == 200 &&
                   BattlefieldAwarderReplies.NiMiniReputationLine(BattlefieldOutcome.Lost, 0) == 201 &&
                   BattlefieldAwarderReplies.NiMiniReputationLine(BattlefieldOutcome.Draw, 0) == 1200, "Ni Mini lines");
        // The captured board 11766 -> [184022, 405021] reads Sparta 840 and Athens 3050.
        Check.True(BattlefieldAwarderReplies.FactionPointRows(3050, 840).SequenceEqual([405_021, 184_022]),
            "faction points rows match the capture's encoding");
        Check.Equal(170_001, BattlefieldAwarderReplies.RankingRow(700, 1), "ranking row matches the capture (700 points, 1st)");
        Check.Equal(120_014, BattlefieldAwarderReplies.FarmReward(120, lost: false), "farm win line");
        Check.Equal(120, BattlefieldAwarderReplies.FarmLevelFromExperience(10_000 + 120 * 120 * 50, lost: false),
            "farm level from the stored EXP");
        Check.Equal(57, BattlefieldAwarderReplies.FarmLevelFromExperience((10_000 + 57 * 57 * 50) / 2, lost: true),
            "farm level from a loser's halved EXP");
    }

    // BATTLEFIELD2: the Pindus band (the admin: 60-200) and the client lines the transporter answers with.
    private static void CheckPindusBand()
    {
        Check.True(BattlefieldRewardPolicy.PindusBandEnabled &&
                   !BattlefieldRewardPolicy.PindusLevelEligible(59) && BattlefieldRewardPolicy.PindusLevelEligible(60) &&
                   BattlefieldRewardPolicy.PindusLevelEligible(200) && !BattlefieldRewardPolicy.PindusLevelEligible(201) &&
                   BattlefieldRewardPolicy.PindusBandMinimumLevel == 60 && BattlefieldRewardPolicy.PindusBandMaximumLevel == 200,
            "Pindus scores level 60-200 by default");
        Check.True(Godswar.Server.Domain.World.Content.BattlefieldTransporterProtocol.PindusBandLevelResultSubId == 7001 &&
                   Godswar.Server.Domain.World.Content.BattlefieldTransporterProtocol.PindusIggClosedResultSubId == 7002 &&
                   Godswar.Server.Domain.World.Content.BattlefieldTransporterProtocol.PindusResultDialogIndex == 2,
            "the band and IGG-times refusals are the client's own NpcFunTranmit Index 2 lines 7001 / 7002");
        ServerRates.Install(ServerRatesSnapshot.Create(
            [KeyValuePair.Create(ServerRateKeys.BattlefieldPindusBand, 0m)], 1, DateTimeOffset.UtcNow, "check", "test"));
        try
        {
            Check.True(!BattlefieldRewardPolicy.PindusBandEnabled && BattlefieldRewardPolicy.PindusLevelEligible(31) &&
                       BattlefieldRewardPolicy.PindusLevelEligible(1),
                "pindus_band = 0: no band on scoring (stock)");
        }
        finally
        {
            ServerRates.ResetToDefaults();
        }
    }

    // BATTLEFIELD2: Awarder lines print the paid (x exp.events) amounts where the client's formula can.
    private static void CheckPaidAmounts()
    {
        Check.True(BattlefieldPaidAmounts.FactionLineLevel(BattlefieldOutcome.Won, 120, 96_000, 120) == 120 &&
                   BattlefieldPaidAmounts.PointsLines(300, 1_800) == (300, 1_800),
            "exp.events x1: the lines carry the base amounts");
        ServerRates.Install(ServerRatesSnapshot.Create(
            [KeyValuePair.Create(ServerRateKeys.EventExperience, 10m),
             KeyValuePair.Create(ServerRateKeys.BattlefieldShowPaidAmounts, 1m)], // RELEASE: default is now 0
            1, DateTimeOffset.UtcNow, "check", "test"));
        try
        {
            var won = BattlefieldRewardPolicy.PindusReward(121, BattlefieldOutcome.Won, 50, 300);
            Check.Equal(1_210, BattlefieldPaidAmounts.FactionLineLevel(BattlefieldOutcome.Won, 121, won.Experience, won.TalentPoints),
                "x10 win at 121: the line prints 800 x 1,210 = 968,000 EXP and 1,210 talent (what is paid)");
            var lostEven = BattlefieldRewardPolicy.PindusReward(120, BattlefieldOutcome.Lost, 0, 0);
            Check.Equal(1_200, BattlefieldPaidAmounts.FactionLineLevel(BattlefieldOutcome.Lost, 120, lostEven.Experience, lostEven.TalentPoints),
                "x10 loss at 120: 400 x 1,200 EXP and 600 talent");
            var lostOdd = BattlefieldRewardPolicy.PindusReward(121, BattlefieldOutcome.Lost, 0, 0);
            Check.Equal(121, BattlefieldPaidAmounts.FactionLineLevel(BattlefieldOutcome.Lost, 121, lostOdd.Experience, lostOdd.TalentPoints),
                "x10 loss at odd level 121: talent would print 605 against 600 paid, so the base line stays");
            var (kill, holy) = BattlefieldPaidAmounts.PointsLines(363, 2_178);
            Check.True(kill == 3_630 && holy == 21_780 && kill + holy == BattlefieldPaidAmounts.Experience(363 + 2_178),
                "x10 points lines print the paid kill and Holy Water EXP, summing to the one payment");
            Check.Equal(50, BattlefieldPaidAmounts.TalentPoints(5), "x10 talent points");
        }
        finally
        {
            ServerRates.ResetToDefaults();
        }
        ServerRates.Install(ServerRatesSnapshot.Create(
            [KeyValuePair.Create(ServerRateKeys.EventExperience, 10m), KeyValuePair.Create(ServerRateKeys.BattlefieldShowPaidAmounts, 0m)],
            1, DateTimeOffset.UtcNow, "check", "test"));
        try
        {
            Check.True(BattlefieldPaidAmounts.FactionLineLevel(BattlefieldOutcome.Won, 121, 96_800, 121) == 121 &&
                       BattlefieldPaidAmounts.PointsLines(363, 2_178) == (363, 2_178),
                "show_paid_amounts = 0: the base lines");
        }
        finally
        {
            ServerRates.ResetToDefaults();
        }
    }
}
