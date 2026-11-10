using Godswar.Server.Application.Rates;
using Godswar.Server.Application.Titles;

namespace Godswar.Server.Application.Zodiac;

/// <summary>What a Fortune Teller click asks for.</summary>
internal enum FortuneTellerAction
{
    FreeSignChange,
    GoldSignChange,
    FreeLuckyDay,
    GoldLuckyDay
}

/// <summary>
/// TITLES: the Fortune Teller (Athens_075 / Sparta_075, function 19, client script NpcFunStar.lua).
/// </summary>
/// <remarks>
/// Rules: IGG guide "Zodiac System" (Development/reference-analysis/text/igg-acid=163.txt, "Lucky Day
/// Feature" and "Zodiac Change"): one free Lucky Day per week, 24 hours from activation, +5% to EXP and
/// Talent EXP; more Lucky Days for gold; one free sign change per month, more for gold. The gold price is
/// the client's own button text (NF_L0_XZ3 / XZ5 "Pay 230 Gold"); the wiki says 345 and the IGG guide
/// names no number, so 230 is the default and the rate <c>npc.fortune_teller_gold</c> sets it.
///
/// Page one is [1 free change, 2 gold change, 3 free lucky day, 4 gold lucky day]. Page two: 1 -> the
/// twelve signs as 1-12, 2 -> the same signs as 13-24, 3 -> [26], 4 -> [39] (the "I want to draw my lucky
/// day" button). Page three is one result line: 1 changed, 100 same sign, 101 once a month, 102 not
/// enough gold, 103 lucky day started, 104 lucky time not used up, 106 already drawn this week.
///
/// Week = Monday 00:00 to Monday 00:00 and month = calendar month, both in the realm's own calendar time
/// zone (server.time_zone_id, the same clock as the realm day). The IGG text does not say whether "per
/// week" is calendar or rolling.
/// </remarks>
internal static class FortuneTellerPolicy
{
    public const int Function = 19;

    public const int FreeChangeButton = 1;
    public const int GoldChangeButton = 2;
    public const int FreeLuckyButton = 3;
    public const int GoldLuckyButton = 4;

    public const int FreeLuckyConfirm = 26;
    public const int GoldLuckyConfirm = 39;

    public const int ChangedLine = 1;
    public const int SameSignLine = 100;
    public const int MonthlyLimitLine = 101;
    public const int NotEnoughGoldLine = 102;
    public const int LuckyStartedLine = 103;
    public const int LuckyActiveLine = 104;
    public const int WeeklyLimitLine = 106;

    /// <summary>NF_L0_XZ6 "Incorrect operation." (any number &gt;= 1000 on any page).</summary>
    public const int IncorrectLine = 1000;

    public static readonly TimeSpan LuckyDuration = TimeSpan.FromHours(24);

    public static bool IsPageOneButton(int value) => value is >= 1 and <= 4;

    /// <summary>The page-two numbers a page-one button opens.</summary>
    public static int[] PageTwo(int pageOneButton) => pageOneButton switch
    {
        FreeChangeButton => [.. Enumerable.Range(1, 12)],
        GoldChangeButton => [.. Enumerable.Range(13, 12)],
        FreeLuckyButton => [FreeLuckyConfirm],
        GoldLuckyButton => [GoldLuckyConfirm],
        _ => []
    };

    /// <summary>
    /// The action and target sign (0 Aries .. 11 Pisces) a page-two click names, or null when the number
    /// does not belong to the page the page-one button opened.
    /// </summary>
    public static (FortuneTellerAction Action, byte Sign)? ResolvePageTwo(int pageOneButton, int value) =>
        pageOneButton switch
        {
            FreeChangeButton when value is >= 1 and <= 12 => (FortuneTellerAction.FreeSignChange, (byte)(value - 1)),
            GoldChangeButton when value is >= 13 and <= 24 => (FortuneTellerAction.GoldSignChange, (byte)(value - 13)),
            FreeLuckyButton when value == FreeLuckyConfirm => (FortuneTellerAction.FreeLuckyDay, (byte)0),
            GoldLuckyButton when value == GoldLuckyConfirm => (FortuneTellerAction.GoldLuckyDay, (byte)0),
            _ => null
        };

    public static bool IsLuckyActive(int status, DateTimeOffset? expiresAt, DateTimeOffset now) =>
        status > 0 && expiresAt is { } expires && expires > now;

    /// <summary>Monday 00:00 realm time of the week <paramref name="now"/> is in, as UTC.</summary>
    public static DateTimeOffset WeekStart(DateTimeOffset now, TimeSpan realmOffset)
    {
        var local = now.ToOffset(realmOffset);
        var daysSinceMonday = ((int)local.DayOfWeek + 6) % 7;
        var start = new DateTimeOffset(local.Date, realmOffset).AddDays(-daysSinceMonday);
        return start.ToUniversalTime();
    }

    /// <summary>The first of the month 00:00 realm time <paramref name="now"/> is in, as UTC.</summary>
    public static DateTimeOffset MonthStart(DateTimeOffset now, TimeSpan realmOffset)
    {
        var local = now.ToOffset(realmOffset);
        return new DateTimeOffset(local.Year, local.Month, 1, 0, 0, 0, realmOffset).ToUniversalTime();
    }

    /// <summary>The state a decision is made on (read from the locked character row).</summary>
    public readonly record struct FortuneState(
        byte Sign, int LuckyStatus, DateTimeOffset? LuckyExpiresAt, int Gold, bool FreeUsedInWindow);

    /// <summary>The decision: the result line, the gold to take and what changes.</summary>
    public readonly record struct FortuneDecision(
        int Line, int GoldCost, bool ChangesSign, bool StartsLucky);

    public static FortuneDecision Decide(
        FortuneTellerAction action, byte targetSign, FortuneState state, int price, DateTimeOffset now)
    {
        switch (action)
        {
            case FortuneTellerAction.FreeSignChange:
            case FortuneTellerAction.GoldSignChange:
                if (targetSign > 11)
                {
                    return new(IncorrectLine, 0, false, false);
                }

                if (targetSign == state.Sign)
                {
                    return new(SameSignLine, 0, false, false);
                }

                if (action == FortuneTellerAction.FreeSignChange)
                {
                    return state.FreeUsedInWindow
                        ? new(MonthlyLimitLine, 0, false, false)
                        : new(ChangedLine, 0, true, false);
                }

                return state.Gold < price
                    ? new(NotEnoughGoldLine, 0, false, false)
                    : new(ChangedLine, price, true, false);

            case FortuneTellerAction.FreeLuckyDay:
            case FortuneTellerAction.GoldLuckyDay:
                if (IsLuckyActive(state.LuckyStatus, state.LuckyExpiresAt, now))
                {
                    return new(LuckyActiveLine, 0, false, false);
                }

                if (action == FortuneTellerAction.FreeLuckyDay)
                {
                    return state.FreeUsedInWindow
                        ? new(WeeklyLimitLine, 0, false, false)
                        : new(LuckyStartedLine, 0, false, true);
                }

                return state.Gold < price
                    ? new(NotEnoughGoldLine, 0, false, false)
                    : new(LuckyStartedLine, price, false, true);
        }

        return new(IncorrectLine, 0, false, false);
    }

    /// <summary>The log action name; free uses are counted per window from it.</summary>
    public static string LogAction(FortuneTellerAction action) => action switch
    {
        FortuneTellerAction.FreeSignChange => "sign-free",
        FortuneTellerAction.GoldSignChange => "sign-gold",
        FortuneTellerAction.FreeLuckyDay => "lucky-free",
        _ => "lucky-gold"
    };

    /// <summary>When the free use of this action last reset.</summary>
    public static DateTimeOffset FreeWindowStart(
        FortuneTellerAction action, DateTimeOffset now, TimeSpan realmOffset) =>
        action is FortuneTellerAction.FreeSignChange or FortuneTellerAction.GoldSignChange
            ? MonthStart(now, realmOffset)
            : WeekStart(now, realmOffset);

    /// <summary>
    /// Adds the Lucky Day bonus (percent, default 5) to one EXP or Talent EXP award, saturating at
    /// <paramref name="maximum"/>. Zero or negative values and a zero percent pass through.
    /// </summary>
    public static int ApplyLuckyBonus(int value, decimal percent, int maximum)
    {
        if (value <= 0 || percent <= 0m)
        {
            return value;
        }

        var boosted = decimal.Truncate(value * (100m + percent) / 100m);
        return boosted >= maximum ? maximum : (int)boosted;
    }

    public static int Price() =>
        (int)Math.Clamp(ServerRates.Current.Get(ServerRateKeys.FortuneTellerGold), 0m, 1_000_000m);

    public static decimal LuckyBonusPercent() => ServerRates.Current.Get(ServerRateKeys.ZodiacLuckyDayBonus);

    /// <summary>
    /// Off by default: the server rate <c>npc.fortune_teller</c> = 1 or env
    /// <c>GODSWAR_FORTUNE_TELLER=true</c> (or 1).
    /// </summary>
    public static bool IsEnabled() =>
        TitleExpertPolicy.IsEnvOn("GODSWAR_FORTUNE_TELLER") ||
        ServerRates.Current.Get(ServerRateKeys.FortuneTellerEnabled) >= 1m;
}
