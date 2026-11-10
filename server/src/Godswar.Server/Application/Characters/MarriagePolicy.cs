using Godswar.Server.Application.Rates;

namespace Godswar.Server.Application.Characters;

/// <summary>
/// MARRIAGE: the rules of Cupid (<c>Athens_058</c>/<c>Sparta_058</c>, function 41, client script
/// <c>NpcFunMaterial.lua</c>), Cupid's Shadow (<c>Athens_091</c>/<c>Sparta_091</c>, function 43,
/// <c>NpcFunDivrce.lua</c>) and the Avatar of Pan's Whelk Necklace (<c>Pans_Labyrinth_010</c>,
/// the same function 41 script's Index-1 lines MOON100-400).
/// Retail sources: IGG guide "Wedded Bliss" (Development/reference-analysis/text/igg-acid=183.txt) and the
/// GodsArena wiki "Marriage &amp; Divorce" (text/wiki-56-marriage-divorce.txt); the client's own NPC text
/// (LuaText.lua NF_L0_MRI*/AWAY*) gives the numbers both pages leave out (9999 Silver, 100 Gold, the fines).
/// </summary>
internal static class MarriagePolicy
{
    public const int CupidFunction = 41;
    public const int ShadowFunction = 43;

    // Client items (ItemBaseAttribute / ClientCatalogItemSeed): 3934 Whelk Necklace, 3937 Aphrodite Blessed Pack.
    public const int WhelkNecklaceItemId = 3934;
    public const int WeddingGiftItemId = 3937;

    // Client title ids (editor_title_catalog): 1401 "Engaged", 1402 "Newly Wed".
    public const int EngagedTitleId = 1401;
    public const int NewlyWedTitleId = 1402;

    // Retail numbers.
    public const int RetailEngagementSilver = 9999;       // NF_L0_MRI12 "(9999 Silver should be paid to get engaged)"
    public const int RetailBookingGold = 100;             // NF_L0_MRI13 "(100 Gold should be paid for the application)"
    public const int ForcedFineReputation = 200;          // NF_L0_AWAY1001/1002, wiki-56, IGG
    public const int ForcedFineSilver = 100_000;          // NF_L0_AWAY1001/1002, wiki-56
    public const int MutualDivorceReputation = 100;       // IGG + wiki-56 "both lose 100 Rep each"
    public static readonly TimeSpan DivorceWaitingPeriod = TimeSpan.FromDays(7);    // NF_L0_AWAY1300, IGG, wiki
    public static readonly TimeSpan AbsenceForFreeForcing = TimeSpan.FromDays(15);  // IGG, wiki, AWAY1001/1002
    public static readonly TimeSpan BookingHorizon = TimeSpan.FromDays(7);         // NF_L0_MRI13 "within the next 7 days"

    // GENERATED (not in any source): how long a proposal / confirmation waits for the other side.
    public static readonly TimeSpan PendingLifetime = TimeSpan.FromMinutes(5);

    // ---- Cupid page one (Index 1) -------------------------------------------------------------
    public const int EntryEngage = 1;
    public const int EntryBook = 2;
    public const int EntryCeremony = 3;
    public const int EntryGift = 4;
    public const int EntryInquire = 5;

    // ---- Cupid page two (Index 2) -------------------------------------------------------------
    public const int ButtonWishEngaged = 11;     // NF_L0_MRI11
    public const int PageBookingForm = 12;       // NF_L0_MRI13 + the weekday/hour boxes
    public const int ButtonAgree = 14;           // NF_L0_MRI14
    public const int ButtonDisagree = 15;        // NF_L0_MRI15
    public const int TextVowsGroom = 16;         // NF_L0_MRI160
    public const int TextVowsBride = 17;         // NF_L0_MRI170
    public const int TextEngagement = 21;        // NF_L0_MRI12
    public const int ButtonAbandonSlot = 41;     // NF_L0_MRI41
    public const int NotEngaged = 1010;
    public const int OnlyMaleBooks = 1020;
    public const int NoNecklace = 1030;
    public const int MustTeamUp = 1050;
    public const int NotEngagedToPartner = 1060;
    public const int NoSlotBooked = 1070;
    public const int SlotExpired = 1080;
    public const int NotYetTime = 1090;
    public const int GroomNotConfirmed = 1130;
    public const int InquireNothing = 1200;
    public const int InquireExpired = 1210;
    public const int InquireNotEligible = 1220;
    public const int GiftNotMarried = 3100;
    public const int GiftBagFull = 3200;
    public const int GiftAlreadyClaimed = 3300;
    public const int GiftClaimed = 3400;

    // ---- Cupid page three (Index 3) -----------------------------------------------------------
    public const int BookBadFormat = 2010;
    public const int BookAlreadyBooked = 2020;
    public const int BookNotEnoughGold = 1040;   // drawn as NF_L0_MRI1041 "You don't have enough Gold"
    public const int BookSlotTaken = 2030;
    public const int BookDone = 2040;
    public const int OppositeGenderOnly = 2050;
    public const int BothMustBeEngaged = 2060;
    public const int BothNeedNecklace = 2070;
    public const int CeremonyExpired = 2080;
    public const int GroomConfirmed = 2090;
    public const int CeremonyExpiredBride = 2100;
    public const int EngageOppositeGender = 2110;
    public const int EngageAlreadyLinked = 2120;
    public const int EngageMenOnly = 2130;
    public const int EngageNotEnoughSilver = 2140;   // drawn as NF_L0_MRI1040 "You don't have enough Silver"
    public const int EngageWaitForBride = 2150;
    public const int SlotDeleted = 2160;
    public const int BrideBeforeGroom = 2190;

    // ---- Pan, Index 1 of the same script --------------------------------------------------------
    public const int PanNotEngaged = 100;
    public const int PanBagFull = 200;
    public const int PanNecklaceGiven = 400;

    // ---- Cupid's Shadow -----------------------------------------------------------------------
    public const int ShadowEntryEngagement = 1;
    public const int ShadowEntryMarriage = 2;
    public const int ShadowMutualEngagement = 11;
    public const int ShadowForcedEngagement = 12;
    public const int ShadowMutualDivorce = 21;
    public const int ShadowForcedDivorce = 22;
    public const int ShadowNotEngaged = 1100;
    public const int ShadowNotMarried = 1200;
    public const int ShadowTooSoon = 1300;
    public const int ShadowMustTeamUp = 2100;
    public const int ShadowNotPartners = 2200;
    public const int ShadowNotMarriedCouple = 2300;
    public const int ShadowDone = 2400;
    public const int ShadowCannotPayFine = 2500;
    public const int ShadowWaitForOther = 2600;

    /// <summary>
    /// Off unless <c>GODSWAR_MARRIAGE=true</c> (or 1) or the hot server rate <c>npc.marriage</c> is 1.
    /// Off, Cupid, Cupid's Shadow and Pan keep exactly their old menu-only answers.
    /// </summary>
    public static bool IsEnabled(string? environmentValue, ServerRatesSnapshot rates) =>
        IsTrue(environmentValue) || rates.Absolute(ServerRateKeys.MarriageEnabled) == 1;

    public static bool IsEnabled() =>
        IsEnabled(Environment.GetEnvironmentVariable("GODSWAR_MARRIAGE"), ServerRates.Current);

    public static bool NecklaceRequired(ServerRatesSnapshot rates) =>
        rates.Absolute(ServerRateKeys.MarriageNecklaceRequired) != 0;

    public static int EngagementSilver(ServerRatesSnapshot rates) =>
        (int)Math.Clamp(rates.Absolute(ServerRateKeys.MarriageEngagementSilver), 0, int.MaxValue);

    public static int BookingGold(ServerRatesSnapshot rates) =>
        (int)Math.Clamp(rates.Absolute(ServerRateKeys.MarriageBookingGold), 0, int.MaxValue);

    private static bool IsTrue(string? value) =>
        value is not null &&
        (value.Equals("true", StringComparison.OrdinalIgnoreCase) || value == "1");

    /// <summary>
    /// The entries the player has clicked, in order, from a 10069 payload's words at <c>+12</c>..<c>+28</c>.
    /// Two layouts are on record: the guild altar capture puts the first click at <c>+12</c>
    /// (docs/公会系统交接规格书.md 3.3), the Class Shifter capture of 2026-09-28 at <c>+16</c> with
    /// <c>+12 = -1</c>. Both are read as the run of non-negative words that starts at the first one.
    /// Trailing zeros are the client's confirm key (a form submission appends one, and its companion
    /// packet a second); they are counted, not returned.
    /// </summary>
    public static MarriageClick ResolveClick(ReadOnlySpan<int> words)
    {
        var path = new List<int>(5);
        var started = false;
        foreach (var word in words)
        {
            if (word < 0)
            {
                if (started)
                {
                    break;
                }

                continue;
            }

            started = true;
            path.Add(word);
        }

        var zeros = 0;
        while (path.Count > 0 && path[^1] == 0)
        {
            path.RemoveAt(path.Count - 1);
            zeros++;
        }

        return new MarriageClick(path.ToArray(), zeros);
    }

    /// <summary>
    /// The booking form's weekday (1 = Monday .. 7 = Sunday) and hour (0-23). The weekday box's word is
    /// <c>+0x38</c> (the one measured amount word); the hour box's word is UNPROVEN and read from
    /// <c>+0x3C</c>. GENERATED fallback: when that second word is not an hour, a three-digit entry in the
    /// first box is read as weekday*100 + hour (e.g. 514 = Friday 14:00).
    /// </summary>
    public static bool TryReadBookingRequest(int first, int second, out int weekday, out int hour)
    {
        weekday = 0;
        hour = 0;
        if (first is >= 1 and <= 7 && second is >= 0 and <= 23)
        {
            weekday = first;
            hour = second;
            return true;
        }

        if (first is >= 100 and <= 723 && first % 100 <= 23)
        {
            weekday = first / 100;
            hour = first % 100;
            return weekday is >= 1 and <= 7;
        }

        return false;
    }

    /// <summary>
    /// The next start of the requested weekday/hour, in realm time, strictly after the current hour.
    /// The current hour itself is refused ("You may not hold a wedding ceremony at the current o'clock").
    /// </summary>
    public static bool TryResolveSlot(
        DateTimeOffset nowRealm,
        int weekday,
        int hour,
        out DateTimeOffset slotRealm)
    {
        slotRealm = default;
        if (weekday is < 1 or > 7 || hour is < 0 or > 23)
        {
            return false;
        }

        var currentHour = new DateTimeOffset(
            nowRealm.Year, nowRealm.Month, nowRealm.Day, nowRealm.Hour, 0, 0, nowRealm.Offset);
        var today = ((int)nowRealm.DayOfWeek + 6) % 7 + 1;   // Monday = 1 .. Sunday = 7
        var days = (weekday - today + 7) % 7;
        var candidate = currentHour.Date.AddDays(days).AddHours(hour);
        var start = new DateTimeOffset(candidate, nowRealm.Offset);
        if (start <= currentHour)
        {
            if (start == currentHour)
            {
                return false;
            }

            start = start.AddDays(7);
        }

        if (start - currentHour > BookingHorizon)
        {
            return false;
        }

        slotRealm = start;
        return true;
    }

    /// <summary>The inquiry line: <c>month*10000 + day*100 + hour</c>, drawn by the script's Index-2 branch.</summary>
    public static int EncodeSlot(DateTimeOffset slotRealm) =>
        slotRealm.Month * 10000 + slotRealm.Day * 100 + slotRealm.Hour;

    public static MarriageSlotState SlotState(DateTimeOffset? slotUtc, DateTimeOffset nowUtc)
    {
        if (slotUtc is not { } slot)
        {
            return MarriageSlotState.None;
        }

        if (nowUtc < slot)
        {
            return MarriageSlotState.Waiting;
        }

        return nowUtc < slot.AddHours(1) ? MarriageSlotState.Open : MarriageSlotState.Expired;
    }

    /// <summary>
    /// The forced-cancellation price: free when the partner has been away 15 days or more, otherwise
    /// 200 Reputation, or 100,000 Silver when the initiator has under 200 Reputation; null when neither
    /// can be paid.
    /// </summary>
    public static MarriageFine? ForcedFine(bool partnerAbsent15Days, long reputation, long silver)
    {
        if (partnerAbsent15Days)
        {
            return new MarriageFine(0, 0);
        }

        if (reputation >= ForcedFineReputation)
        {
            return new MarriageFine(ForcedFineReputation, 0);
        }

        return silver >= ForcedFineSilver ? new MarriageFine(0, ForcedFineSilver) : null;
    }

    public static bool IsAbsent(DateTimeOffset? lastOnlineUtc, bool onlineNow, DateTimeOffset nowUtc) =>
        !onlineNow && (lastOnlineUtc is null || nowUtc - lastOnlineUtc.Value >= AbsenceForFreeForcing);
}

internal readonly record struct MarriageClick(int[] Path, int TrailingZeros)
{
    public int Clicked => Path.Length == 0 ? -1 : Path[^1];

    public int Parent => Path.Length < 2 ? -1 : Path[^2];

    /// <summary>The nearest earlier entry in <paramref name="entries"/>, i.e. the page-one entry a page-two button sits under.</summary>
    public int PageOneEntry(params int[] entries)
    {
        for (var index = Path.Length - 2; index >= 0; index--)
        {
            if (Array.IndexOf(entries, Path[index]) >= 0)
            {
                return Path[index];
            }
        }

        return -1;
    }
}

internal readonly record struct MarriageFine(int Reputation, int Silver);

internal enum MarriageSlotState
{
    None,
    Waiting,
    Open,
    Expired
}
