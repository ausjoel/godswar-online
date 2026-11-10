using Godswar.Server.Application.Rates;

namespace Godswar.Server.Application.Villagers;

// VILLAGERS (2026-11-03): the Village Leader's book re-issue and the Festival
// Envoy's weekend EXP claim and holiday gifts. Pure rules only; the handlers are
// Game/GameClientHandler.Villagers.cs and the SQL is State/PostgresGameStore.Villagers.cs.

/// <summary>The four server_rates switches, each a 0/1 "Absolute" row.</summary>
internal static class VillagersRateDefinitions
{
    public static IReadOnlyList<ServerRateDefinition> All { get; } =
    [
        Switch(ServerRateKeys.WeekendExperienceClaim, 10, "Festival Envoy weekend EXP claim", 0m, // RELEASE: default off
            "Game/GameClientHandler.Villagers.cs",
            "1 = the Festival Envoy's 'Weekend EXP Fun' button works (retail: Saturday and " +
            "Sunday, +200% monster EXP for 8 online hours, once per character per day). " +
            "0 = the button does nothing (the behaviour before this switch existed)."),
        Switch(ServerRateKeys.WeekendExperienceAnyDay, 20, "Weekend EXP claim on any day", 0m,
            "Application/Progression/WeekendExperienceClaimContracts.cs",
            "1 = the weekend EXP claim is also open Monday-Friday (a GM event or a test). " +
            "0 = Saturday and Sunday only (retail)."),
        Switch(ServerRateKeys.FestivalGifts, 30, "Festival Envoy holiday gifts", 0m,
            "Game/GameClientHandler.Villagers.cs, table public.festival_gift_days",
            "1 = the Festival Envoy hands out the gift of today's row in festival_gift_days " +
            "(level 30+, once per character per day, until 23:55 server time) and answers " +
            "'Festival intro' and 'next festival'. 0 = those buttons do nothing (as before). " +
            "The amounts in the table are generated, not retail."),
        Switch(ServerRateKeys.VillageLeaderBooks, 40, "Village Leader re-issues portal and riding books", 0m, // RELEASE: default off
            "Game/GameClientHandler.Villagers.cs",
            "1 = talking to the Village Leader (Admes / Stefanos, 164,-34) at level 40+ hands " +
            "out Book: Marathon Portal (Athens) or Book: Peloponnesus Portal (Sparta) and " +
            "Book: Riding Skills when the character has neither learned the skill nor owns " +
            "the book. 0 = description only (as before).")
    ];

    private static ServerRateDefinition Switch(
        string key, int sortOrder, string label, decimal defaultValue,
        string appliesTo, string description) =>
        new(key, ServerRateCatalog.VillagersCategory, label, "0/1",
            ServerRateKind.Absolute, defaultValue, 0m, 1m, 1m, sortOrder,
            appliesTo, description);
}

internal static class VillagersSwitches
{
    public static bool WeekendClaimEnabled =>
        ServerRates.Current.Absolute(ServerRateKeys.WeekendExperienceClaim) == 1;

    public static bool WeekendClaimAnyDay =>
        ServerRates.Current.Absolute(ServerRateKeys.WeekendExperienceAnyDay) == 1;

    public static bool FestivalGiftsEnabled =>
        ServerRates.Current.Absolute(ServerRateKeys.FestivalGifts) == 1;

    public static bool VillageLeaderBooksEnabled =>
        ServerRates.Current.Absolute(ServerRateKeys.VillageLeaderBooks) == 1;
}

/// <summary>One book the Village Leader can re-issue.</summary>
internal readonly record struct VillageLeaderBook(int BookItemId, int SkillId, string DisplayName);

/// <summary>
/// The Village Leader's re-issue rule, from the GodsArena wiki page "Learning portal
/// and riding skills" (text/wiki-08): at level 40, Admes (Athens) gives Book:
/// Marathon Portal and Book: Riding Skills, Stefanos (Sparta) gives Book:
/// Peloponnesus Portal and Book: Riding Skills; a full bag gets nothing. The IGG
/// guide (acid=188) only describes the original level-40 quest reward of the riding
/// book, so the wiki rule is used.
/// </summary>
internal static class VillageLeaderRules
{
    public const int MinimumLevel = 40;
    public const byte SpartaCamp = 0;
    public const byte AthensCamp = 1;

    // Client item data (ItemBaseAttribute SkillBook) and the published skill
    // seed: 5800 -> 3064 Marathon Portal, 5801 -> 3065 Peloponnesus Portal,
    // 5802 -> 4904 Riding Skills.
    public static readonly VillageLeaderBook MarathonPortal = new(5800, 3064, "Book: Marathon Portal");
    public static readonly VillageLeaderBook PeloponnesusPortal = new(5801, 3065, "Book: Peloponnesus Portal");
    public static readonly VillageLeaderBook RidingSkills = new(5802, 4904, "Book: Riding Skills");

    public static bool IsVillageLeader(string npcKey) =>
        npcKey is "Athens_057" or "Sparta_057";

    /// <summary>The books a character of this camp may receive (portal first).</summary>
    public static IReadOnlyList<VillageLeaderBook> BooksFor(byte camp) =>
        camp switch
        {
            AthensCamp => [MarathonPortal, RidingSkills],
            SpartaCamp => [PeloponnesusPortal, RidingSkills],
            _ => []
        };
}

internal enum VillageLeaderGrantStatus
{
    CharacterNotFound,
    NothingMissing,
    InsufficientCapacity,
    Granted
}

internal enum FestivalGiftClaimStatus
{
    CharacterNotFound,
    AlreadyClaimed,
    InsufficientCapacity,
    Granted
}

/// <summary>One row of public.festival_gift_days.</summary>
internal sealed record FestivalGiftDay(
    DateOnly Day,
    int FestivalIndex,
    int MinimumLevel,
    int Experience,
    int TalentPoints,
    int ItemId,
    int ItemCount);

/// <summary>
/// The Festival Envoy page numbers. Every number comes from the client's own
/// script <c>NpcFunGift.lua</c> (function 28) and its texts in
/// <c>UI/Base/LuaText.lua</c> (<c>NF_L0_GIFT*</c>). The 10070 frame that carries
/// them is the one captured for the weekend page (<c>[501,502,4,5]</c>,
/// <c>[511,3,4,5]</c>); the gift, intro and next-festival pages are composed from
/// the script and were not captured on a reference server.
/// </summary>
internal static class FestivalEnvoyRules
{
    public const int FunctionNumber = 28;
    public const int ClaimGiftButton = 2;
    public const int IntroButton = 3;
    public const int NextFestivalButton = 4;
    public const int WeekendButton = 5;
    public const int WeekendClaimPath = 501;

    public const int BagFullText = 500;          // NF_L0_MRI3200 "Your backpack does not have enough free space"
    public const int BGoldBagLine = 101;          // NF_L0_GIFT101 "1 B-Gold Bag"
    public const int NoGiftTodayText = 102;       // NF_L0_GIFT102
    public const int AlreadyClaimedText = 103;    // NF_L0_GIFT103
    public const int TooLateText = 105;           // NF_L0_GIFT105 "come before 11:55pm"
    public const int NotFestivalDayText = 106;    // NF_L0_GIFT106
    public const int BelowLevelText = 109;        // NF_L0_GIFT109 "beyond Level 30"
    public const int ObtainedText = 110;          // NF_L0_GIFT110 "You have obtained Festival Rewards."
    public const int IntroBase = 200;             // NF_L0_GIFT200..214
    public const int NextBase = 300;              // NF_L0_GIFT300..314
    public const int MaximumFestivalIndex = 14;
    public const int BGoldBagItemId = 4165;       // B-GOLD Bag (client item data, Stone6)

    // The script prints "Exp:" + (SubID-1)/1000 and "Talent points:" +
    // (SubID-2)/1000, so the shown number is capped by Int32 / 1000.
    public const int MaximumShownAmount = 2_000_000;

    /// <summary>Retail text: claims close at 11:55pm server time (NF_L0_GIFT105).</summary>
    public static readonly TimeOnly ClaimCutoff = new(23, 55);

    /// <summary>The 15 festivals of the client list, index = GIFT2xx/3xx - 200/300.</summary>
    public static readonly string[] FestivalNames =
    [
        "New Year's Day", "Chinese New Year", "Valentine's Day", "April Fools' Day",
        "Easter", "Mother's Day", "Philippine Independence Day", "Father's Day",
        "US Independence Day", "Halloween", "All Saints' Day", "All Souls' Day",
        "Thanksgiving", "Christmas", "Last Day of the Year"
    ];

    public static bool IsFestivalEnvoy(string npcKey) =>
        npcKey is "Athens_084" or "Sparta_084";

    /// <summary>The click's path as the capital protocol reads it ("" or "501").</summary>
    public static string? ReadPath(IReadOnlyList<int> arguments)
    {
        var path = new List<int>();
        var padding = false;
        foreach (var argument in arguments)
        {
            if (argument == -1)
            {
                padding = true;
                continue;
            }
            if (padding)
            {
                return null;
            }
            path.Add(argument);
        }
        return string.Join('/', path);
    }

    public static int ExperienceLine(int experience) =>
        checked((Math.Clamp(experience, 0, MaximumShownAmount) * 1000) + 1);

    public static int TalentLine(int talentPoints) =>
        checked((Math.Clamp(talentPoints, 0, MaximumShownAmount) * 1000) + 2);

    /// <summary>The page shown after a gift was handed out.</summary>
    public static int[] GrantedPage(FestivalGiftDay gift)
    {
        var page = new List<int> { ObtainedText };
        if (gift.Experience > 0)
        {
            page.Add(ExperienceLine(gift.Experience));
        }
        if (gift.TalentPoints > 0)
        {
            page.Add(TalentLine(gift.TalentPoints));
        }
        if (gift.ItemId == BGoldBagItemId && gift.ItemCount == 1)
        {
            page.Add(BGoldBagLine);
        }
        return page.ToArray();
    }

    public static int IntroText(int festivalIndex) =>
        IntroBase + Math.Clamp(festivalIndex, 0, MaximumFestivalIndex);

    public static int NextText(int festivalIndex) =>
        NextBase + Math.Clamp(festivalIndex, 0, MaximumFestivalIndex);

    public static bool IsTooLate(TimeOnly localTime) => localTime >= ClaimCutoff;
}
