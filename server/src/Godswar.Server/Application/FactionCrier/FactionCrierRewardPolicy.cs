using Godswar.Server.Application.Rates; // ROUND2
using Godswar.Server.Application.Realms;

namespace Godswar.Server.Application.FactionCrier;

internal static class FactionCrierRewardPolicy
{
    public const int FirstNameplateItemId = 3820;
    public const int LastNameplateItemId = 3825;
    public const int NameplateCount = 6;
    public const int MaximumStack = 99;

    private static readonly int[] OddNameplates = [3820, 3822, 3824];
    private static readonly int[] EvenNameplates = [3821, 3823, 3825];
    private static readonly int[] AllNameplates =
        [3820, 3821, 3822, 3823, 3824, 3825];

    public static bool TryCreatePlan(
        FactionCrierCommand command,
        int characterLevel,
        DateTimeOffset receivedAt,
        FactionCrierBalanceSnapshot balance,
        RealmCalendar realmCalendar,
        out FactionCrierExecutionPlan plan)
    {
        ArgumentNullException.ThrowIfNull(balance);
        ArgumentNullException.ThrowIfNull(realmCalendar);
        balance.Validate();
        plan = default;
        if (characterLevel < balance.MinimumLevel ||
            characterLevel > 200 ||
            command.RealmId <= 0 ||
            command.DialogIndex != 15)
        {
            return false;
        }

        var tier = balance.Tiers.SingleOrDefault(value =>
            characterLevel >= value.MinimumLevel &&
            characterLevel <= value.MaximumLevel);
        if (tier == default)
        {
            return false;
        }

        return command.Operation switch
        {
            FactionCrierOperation.DailyClaim => TryCreateDailyClaim(
                command,
                receivedAt,
                balance,
                realmCalendar,
                out plan),
            FactionCrierOperation.WeeklyReclaim => TryCreateWeeklyReclaim(
                command,
                receivedAt,
                balance,
                realmCalendar,
                out plan),
            FactionCrierOperation.RenewNameplate => TryCreateRenewal(
                command,
                balance,
                out plan),
            FactionCrierOperation.TurnIn => TryCreateTurnIn(
                command,
                tier,
                balance,
                out plan),
            _ => false
        };
    }

    public static int NativeFailureSubId(
        FactionCrierOperation operation,
        FactionCrierExecutionDisposition disposition) =>
        disposition switch
        {
            FactionCrierExecutionDisposition.ClosedToday => 517,
            FactionCrierExecutionDisposition.BelowMinimumLevel =>
                operation == FactionCrierOperation.DailyClaim ? 501 :
                operation == FactionCrierOperation.WeeklyReclaim ? 700 :
                operation == FactionCrierOperation.RenewNameplate ? 800 :
                1900,
            FactionCrierExecutionDisposition.BagFull =>
                operation == FactionCrierOperation.DailyClaim ? 503 : 900,
            FactionCrierExecutionDisposition.AlreadyClaimed =>
                operation == FactionCrierOperation.DailyClaim ? 502 : 702,
            FactionCrierExecutionDisposition.InsufficientCurrency =>
                operation switch
                {
                    FactionCrierOperation.WeeklyReclaim => 701,
                    FactionCrierOperation.RenewNameplate => 801,
                    _ => 2000
                },
            FactionCrierExecutionDisposition.MissingNameplate =>
                operation == FactionCrierOperation.RenewNameplate
                    ? 800
                    : 1900,
            FactionCrierExecutionDisposition.PreconditionFailed or
                FactionCrierExecutionDisposition.InvalidIntent =>
                operation switch
                {
                    FactionCrierOperation.DailyClaim => 500,
                    FactionCrierOperation.WeeklyReclaim => 700,
                    FactionCrierOperation.RenewNameplate => 800,
                    _ => 1900
                },
            _ => operation switch
            {
                FactionCrierOperation.DailyClaim => 500,
                FactionCrierOperation.WeeklyReclaim => 700,
                FactionCrierOperation.RenewNameplate => 800,
                FactionCrierOperation.TurnIn => 1900,
                _ => 500
            }
        };

    public static FactionCrierBalanceSnapshot CreateReviewedDefault() =>
        new(
            Revision: 0,
            MinimumLevel: 20,
            WeeklyReclaimGoldCost: 230,
            RenewalGoldCost: 105,
            Tiers:
            [
                new(20, 39, 23_500, 5, 15_000, 40_000),
                new(40, 59, 45_800, 7, 35_000, 120_000),
                new(60, 79, 116_000, 14, 60_000, 180_000),
                new(80, 99, 158_000, 21, 80_000, 240_000),
                new(100, 120, 218_000, 32, 120_000, 360_000),
                new(121, 130, 230_000, 35, 140_000, 400_000),
                // Reborn extends the final stock 131-140 tier through the
                // server's level cap so max-level fixtures can use the NPC.
                new(131, 200, 250_000, 38, 140_000, 400_000)
            ],
            Options: CreateReviewedOptions());

    private static IReadOnlyList<FactionCrierBalanceOption>
        CreateReviewedOptions()
    {
        var options = new List<FactionCrierBalanceOption>(25);
        AddTripleOptions(options, 110);
        AddTripleOptions(options, 120);
        options.Add(new(130, FactionCrierCurrency.Silver, 0, 12,
            FactionCrierRewardKind.ExperienceAndTalentPoints));
        options.Add(new(131, FactionCrierCurrency.BoundGold, 936, 18,
            FactionCrierRewardKind.ExperienceAndTalentPoints));
        options.Add(new(132, FactionCrierCurrency.Gold, 936, 18,
            FactionCrierRewardKind.ExperienceAndTalentPoints));
        options.Add(new(133, FactionCrierCurrency.BoundGold, 1_377, 24,
            FactionCrierRewardKind.ExperienceAndTalentPoints));
        options.Add(new(134, FactionCrierCurrency.Gold, 1_377, 24,
            FactionCrierRewardKind.ExperienceAndTalentPoints));
        return options;
    }

    private static void AddTripleOptions(
        ICollection<FactionCrierBalanceOption> options,
        int firstSubId)
    {
        options.Add(new(firstSubId, FactionCrierCurrency.Silver, 0, 6,
            FactionCrierRewardKind.Experience));
        options.Add(new(firstSubId + 1, FactionCrierCurrency.Silver, 0, 6,
            FactionCrierRewardKind.TalentPoints));
        options.Add(new(firstSubId + 2, FactionCrierCurrency.BoundGold, 312, 9,
            FactionCrierRewardKind.Experience));
        options.Add(new(firstSubId + 3, FactionCrierCurrency.BoundGold, 312, 9,
            FactionCrierRewardKind.TalentPoints));
        options.Add(new(firstSubId + 4, FactionCrierCurrency.Gold, 312, 9,
            FactionCrierRewardKind.Experience));
        options.Add(new(firstSubId + 5, FactionCrierCurrency.Gold, 312, 9,
            FactionCrierRewardKind.TalentPoints));
        options.Add(new(firstSubId + 6, FactionCrierCurrency.BoundGold, 459, 12,
            FactionCrierRewardKind.Experience));
        options.Add(new(firstSubId + 7, FactionCrierCurrency.BoundGold, 459, 12,
            FactionCrierRewardKind.TalentPoints));
        options.Add(new(firstSubId + 8, FactionCrierCurrency.Gold, 459, 12,
            FactionCrierRewardKind.Experience));
        options.Add(new(firstSubId + 9, FactionCrierCurrency.Gold, 459, 12,
            FactionCrierRewardKind.TalentPoints));
    }

    private static bool TryCreateDailyClaim(
        FactionCrierCommand command,
        DateTimeOffset receivedAt,
        FactionCrierBalanceSnapshot balance,
        RealmCalendar realmCalendar,
        out FactionCrierExecutionPlan plan)
    {
        plan = default;
        var day = realmCalendar.GetDay(receivedAt);
        if (command.SubId != 1 ||
            command.RenewalSource.HasValue ||
            command.RealmPeriodDayNumber != day.DayNumber ||
            day.DayOfWeek == DayOfWeek.Sunday)
        {
            return false;
        }

        var weekdayOrdinal = ((int)day.DayOfWeek + 6) % 7;
        var itemId = FirstNameplateItemId + weekdayOrdinal;
        plan = new(
            command.Operation,
            360 + weekdayOrdinal,
            [],
            itemId,
            FactionCrierCurrency.None,
            0,
            0,
            0,
            day,
            null);
        return true;
    }

    private static bool TryCreateWeeklyReclaim(
        FactionCrierCommand command,
        DateTimeOffset receivedAt,
        FactionCrierBalanceSnapshot balance,
        RealmCalendar realmCalendar,
        out FactionCrierExecutionPlan plan)
    {
        plan = default;
        var day = realmCalendar.GetDay(receivedAt);
        var weekStart = RealmCalendar.GetWeekStart(day);
        if (command.SubId is < 31 or > 36 ||
            command.RenewalSource.HasValue ||
            command.RealmPeriodDayNumber != weekStart.DayNumber)
        {
            return false;
        }

        var ordinal = command.SubId - 31;
        plan = new(
            command.Operation,
            731 + ordinal,
            [],
            FirstNameplateItemId + ordinal,
            FactionCrierCurrency.Gold,
            balance.WeeklyReclaimGoldCost,
            0,
            0,
            null,
            weekStart);
        return true;
    }

    private static bool TryCreateRenewal(
        FactionCrierCommand command,
        FactionCrierBalanceSnapshot balance,
        out FactionCrierExecutionPlan plan)
    {
        plan = default;
        if (command.SubId is < 41 or > 46 ||
            command.RealmPeriodDayNumber != 0 ||
            command.RenewalSource is not { } source ||
            !IsNameplate(source.ItemId))
        {
            return false;
        }

        var ordinal = command.SubId - 41;
        var target = FirstNameplateItemId + ordinal;
        if (source.ItemId == target)
        {
            return false;
        }

        plan = new(
            command.Operation,
            841 + ordinal,
            [source.ItemId],
            target,
            FactionCrierCurrency.Gold,
            balance.RenewalGoldCost,
            0,
            0,
            null,
            null);
        return true;
    }

    private static bool TryCreateTurnIn(
        FactionCrierCommand command,
        FactionCrierBalanceTier tier,
        FactionCrierBalanceSnapshot balance,
        out FactionCrierExecutionPlan plan)
    {
        plan = default;
        if (command.RealmPeriodDayNumber != 0 ||
            command.RenewalSource.HasValue)
        {
            return false;
        }

        if (command.SubId is >= 101 and <= 106)
        {
            var itemId = FirstNameplateItemId + command.SubId - 101;
            plan = new(
                command.Operation,
                SingleResultSubId(command.SubId, tier),
                [itemId],
                null,
                FactionCrierCurrency.None,
                0,
                tier.BaseExperience,
                0,
                null,
                null);
            return true;
        }

        var option = balance.Options.SingleOrDefault(
            value => value.SubId == command.SubId);
        if (option == default)
        {
            return false;
        }

        var allSix = command.SubId >= 130;
        var consumed = allSix
            ? AllNameplates
            : command.SubId < 120 ? OddNameplates : EvenNameplates;
        var cost = option.Currency == FactionCrierCurrency.Silver &&
            option.Cost == 0
                ? allSix ? tier.AllSixSilverCost : tier.TripleSilverCost
                : option.Cost;
        var experience = option.RewardKind is
            FactionCrierRewardKind.Experience or
            FactionCrierRewardKind.ExperienceAndTalentPoints
                ? checked(tier.BaseExperience * option.Multiplier)
                : 0;
        var talent = option.RewardKind is
            FactionCrierRewardKind.TalentPoints or
            FactionCrierRewardKind.ExperienceAndTalentPoints
                ? checked(tier.BaseTalentPoints * option.Multiplier)
                : 0;
        // ROUND2: exp.faction_crier / tp.faction_crier (rate 1 = stock).
        experience = ServerRates.Current.Multiply(
            ServerRateKeys.FactionCrierExperience,
            experience,
            100_000_000);
        talent = ServerRates.Current.Multiply(
            ServerRateKeys.FactionCrierTalent,
            talent,
            1_000_000); // ROUND2: Talent Points go straight into an int balance
        plan = new(
            command.Operation,
            ExchangeResultSubId(option, tier),
            consumed,
            null,
            option.Currency,
            cost,
            experience,
            talent,
            null,
            null);
        return true;
    }

    private static int SingleResultSubId(
        int subId,
        FactionCrierBalanceTier tier)
    {
        var tierIndex = TierIndex(tier);
        return tierIndex switch
        {
            <= 4 => 1901 + tierIndex,
            5 => 1906,
            _ => 31905
        };
    }

    private static int ExchangeResultSubId(
        FactionCrierBalanceOption option,
        FactionCrierBalanceTier tier)
    {
        var tierIndex = TierIndex(tier);
        var baseSubId = option.SubId switch
        {
            110 or 120 => 1911,
            111 or 121 => 2011,
            112 or 114 or 122 or 124 => 1916,
            113 or 115 or 123 or 125 => 2016,
            116 or 118 or 126 or 128 => 1921,
            117 or 119 or 127 or 129 => 2021,
            130 => 3001,
            131 or 132 => 3006,
            133 or 134 => 3011,
            _ => throw new ArgumentOutOfRangeException(nameof(option))
        };
        return tierIndex switch
        {
            <= 4 => baseSubId + tierIndex,
            5 => baseSubId switch
            {
                1911 => 19161,
                2011 => 20161,
                1916 => 19211,
                2016 => 20211,
                1921 => 19261,
                2021 => 20261,
                3001 => 30061,
                3006 => 30111,
                3011 => 30161,
                _ => throw new InvalidOperationException()
            },
            _ => baseSubId switch
            {
                1911 => 31915,
                2011 => 32015,
                1916 => 31920,
                2016 => 32020,
                1921 => 31925,
                2021 => 32025,
                3001 => 33005,
                3006 => 33010,
                3011 => 33015,
                _ => throw new InvalidOperationException()
            }
        };
    }

    private static int TierIndex(FactionCrierBalanceTier tier) =>
        tier.MinimumLevel switch
        {
            20 => 0,
            40 => 1,
            60 => 2,
            80 => 3,
            100 => 4,
            121 => 5,
            131 => 6,
            _ => throw new InvalidDataException(
                "The Faction Crier tier is not part of the reviewed ladder.")
        };

    private static bool IsNameplate(int itemId) =>
        itemId is >= FirstNameplateItemId and <= LastNameplateItemId;
}
