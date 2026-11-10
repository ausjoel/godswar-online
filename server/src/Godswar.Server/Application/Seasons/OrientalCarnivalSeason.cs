using Godswar.Server.Application.Rates;
using Godswar.Server.Domain.World.Content;

namespace Godswar.Server.Application.Seasons;

/// <summary>
/// W3 seasons: the Oriental Cuisine Master and the Carnival Envoy (the admin F-26 "build the seasons", F-134 "each runs
/// for the 10 days before the event day"). Kept apart from <see cref="SeasonalEvents"/> so the sibling season branches
/// merge cleanly. Everything here needs the season open in the editor (public.seasonal_events) AND its hot switch at 1
/// (season.oriental_cuisine / season.carnival, default 1). The same predicate hides the NPCs at map entry and gates
/// their menus, so an NPC never stands without working. Dates come from the editor (no auto dates: Mid-autumn is a
/// lunar date and the Lunar Priest of the same chain uses editor dates; the Carnival day is GENERATED, see the PR).
/// <list type="bullet">
/// <item>Oriental Cuisine Master (Athens_031 / Sparta_025) = function 102 NpcFunMooncake.lua ("食谱兑换月饼"): 4 Greek
/// Cuisine Recipes (3919) -> 1 Mysterious Oriental Scroll (NF_L0_M101 / MN100). The scroll is chosen for the camp and
/// level band its description names; finishing the scroll quest pays 1 Delicate Mooncake (2 for the "all players"
/// scroll VII), the Lunar Priest's currency. Plus function 101 NpcFunFood.lua (the free Mid-autumn wish: 3-5 bound
/// recipes, 3 a day, an hour apart, NF_L0_FOD101); historically that wish sat on the Wishing Pool (2010 news), here
/// it is on the Master so the Wishing Pool stays untouched (GENERATED placement).</item>
/// <item>Carnival Envoy (Athens_126 / Sparta_126) = function 74 NpcPermit.lua ("周年许愿", wish-making credits): the
/// NPC text and NF_L0_TZN120 both speak of wish-making credits earned in the Item Mall. Prizes and costs are the en_us
/// button labels (NF_L0_BZN*); only the odds and the credit rate are GENERATED.</item>
/// </list>
/// Neither NPC uses a mesh (NPC.INI MeshName=null), so the 1.1-tall mesh pick box does not apply.
/// </summary>
internal static class OrientalCarnivalSeason
{
    public const string OrientalKey = "oriental_cuisine";
    public const string CarnivalKey = "carnival";

    public const int ScrollExchangeFunction = 102; // NPC_FLAG_SYS_MOONCAKE (NpcFun.lua reassigns 75 -> 102)
    public const int FoodWishFunction = 101; // NPC_FLAG_SYS_FOOD
    public const int CarnivalFunction = 74; // NPC_FLAG_SYS_PERMIT

    // Items (client EquipName / EquipDescription / ItemBaseAttribute).
    public const int GreekCuisineRecipe = 3919; // Overlap 99
    public const int DelicateMooncake = 3920; // Overlap 99
    public const int RecipesPerScroll = 4; // NF_L0_M101 / MN100 / M117
    public const int WishRecipesMin = 3, WishRecipesMax = 5, WishesPerDay = 3, WishGapSeconds = 3600; // NF_L0_FOD101

    // Hot switches and GENERATED amounts (server_rates; editor "Server rates" tab).
    public const string OrientalSwitchKey = "season.oriental_cuisine";
    public const string RecipePercentKey = "season.oriental_recipe_percent";
    public const string AllScrollPercentKey = "season.oriental_all_scroll_percent";
    public const string CarnivalSwitchKey = "season.carnival";
    public const string CreditsPer100GoldKey = "season.carnival_credits_per_100_gold";
    public const string RarePercentKey = "season.carnival_rare_percent";
    public const string CommonPercentKey = "season.carnival_common_percent";

    public const int MaxCredits = 1_000_000; // a sanity cap on the stored balance

    public static bool OrientalSwitchOn => Rate(OrientalSwitchKey) >= 1;
    public static bool CarnivalSwitchOn => Rate(CarnivalSwitchKey) >= 1;

    public static bool OrientalOpen(DateTimeOffset at) => OrientalSwitchOn && SeasonalEvents.IsOpen(OrientalKey, at);
    public static bool CarnivalOpen(DateTimeOffset at) => CarnivalSwitchOn && SeasonalEvents.IsOpen(CarnivalKey, at);

    public static bool IsCuisineMaster(string key) => key is "Athens_031" or "Sparta_025";
    public static bool IsCarnivalEnvoy(string key) => key is "Athens_126" or "Sparta_126";

    // ---- Map entry ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// The Athens NPCs no published Athens roster holds (NpcContentBaselineV9 added only the Sparta ones): placed from
    /// the reference capture (template, 0x111, 64,-182 / 64,-156). Object ids: Athens_031 keeps the captured 5171 (free
    /// in the published roster). The captured 5264 of Athens_126 belongs to the published Athens_125 (the published
    /// Athens ids above 5168 run one past the capture's), so Athens_126 takes 5265, the id that numbering gives it
    /// (GENERATED; no captured or published NPC on map 1 uses it).
    /// </summary>
    private static readonly (string Key, uint ObjectId)[] AthensAdded = [("Athens_031", 5171), ("Athens_126", 5265)];

    /// <summary>
    /// Map entry: hides the Master / Envoy while this file's predicate is closed (switch 0 or season closed), and
    /// places the two Athens NPCs from the capture while their season is open. Placement only: no frame is sent.
    /// </summary>
    public static List<NpcSpawnDefinition> ApplyRoster(short mapId, List<NpcSpawnDefinition> roster, DateTimeOffset at)
    {
        var oriental = OrientalOpen(at);
        var carnival = CarnivalOpen(at);
        var result = roster.Where(npc => !(IsCuisineMaster(npc.NpcKey) && !oriental) &&
                                         !(IsCarnivalEnvoy(npc.NpcKey) && !carnival)).ToList();
        if (mapId != 1) return result;
        foreach (var (key, objectId) in AthensAdded)
        {
            var open = IsCuisineMaster(key) ? oriental : carnival;
            if (!open || result.Any(npc => npc.NpcKey == key) ||
                !Infrastructure.WorldContent.CapturedNpcPlacements.TryFind(key, out var placed))
                continue;
            if (result.Any(npc => npc.ObjectId == objectId || npc.InteractionId == objectId))
            {
                Console.WriteLine($"[season] {key} not placed: object {objectId} is taken on map 1");
                continue;
            }

            result.Add(new NpcSpawnDefinition(1, "Athens", key, placed.TemplateKey, objectId, placed.X, placed.Z,
                objectId, placed.AppearanceType, placed.Facing, [], []));
        }

        return result;
    }

    // ---- Mysterious Oriental Scrolls ---------------------------------------------------------------------------------

    /// <summary>
    /// The scroll items and their quests (ItemBaseAttribute "Quest"; descriptions give camp and band).
    /// Athens (camp 1): I/II under 60, III/IV 61-90, V/VI 91-120, VII all (2 cakes), VIII/IX above 120.
    /// Sparta: the same order on 4667-4673 / 4816-4817.
    /// </summary>
    private static readonly (int Item, uint Quest, int MinLevel, int MaxLevel, int Cakes, bool Athens)[] Scrolls =
    [
        (4660, 1320, 1, 60, 1, true), (4661, 1321, 1, 60, 1, true),
        (4662, 1322, 61, 90, 1, true), (4663, 1323, 61, 90, 1, true),
        (4664, 1324, 91, 120, 1, true), (4665, 1325, 91, 120, 1, true),
        (4666, 1326, 1, 200, 2, true),
        (4814, 1474, 121, 200, 1, true), (4815, 1475, 121, 200, 1, true),
        (4667, 320, 1, 60, 1, false), (4668, 321, 1, 60, 1, false),
        (4669, 322, 61, 90, 1, false), (4670, 323, 61, 90, 1, false),
        (4671, 324, 91, 120, 1, false), (4672, 325, 91, 120, 1, false),
        (4673, 326, 1, 200, 2, false),
        (4816, 474, 121, 200, 1, false), (4817, 475, 121, 200, 1, false)
    ];

    public static IEnumerable<int> ScrollItems => Scrolls.Select(static s => s.Item);
    public static IEnumerable<uint> ScrollQuests => Scrolls.Select(static s => s.Quest);

    /// <summary>The 18 Mysterious Oriental Scroll quests: accepted at any level from their minimum (the item says
    /// "under Lvl 60" etc., and the server never hands out a scroll outside the band), not only within nine levels.</summary>
    public static bool IsScrollQuest(uint questId) => Scrolls.Any(s => s.Quest == questId);

    /// <summary>Delicate Mooncakes a scroll quest pays on hand-in (0 = not a scroll quest).</summary>
    public static int MooncakesFor(uint questId) => Scrolls.FirstOrDefault(s => s.Quest == questId).Cakes;

    /// <summary>
    /// The scroll for a camp and level: one of the two band scrolls, or (at <paramref name="allScrollPercent"/>,
    /// GENERATED) the "all players" scroll VII, which pays 2 cakes. <paramref name="roll"/> is 0-9999.
    /// </summary>
    public static int PickScroll(bool athens, int level, int roll, int allScrollPercent)
    {
        var all = Scrolls.First(s => s.Athens == athens && s.Cakes == 2).Item;
        if (roll % 10_000 < Math.Clamp(allScrollPercent, 0, 100) * 100) return all;
        var band = Scrolls.Where(s => s.Athens == athens && s.Cakes == 1 && level >= s.MinLevel && level <= s.MaxLevel)
            .ToArray();
        if (band.Length == 0) return all;
        return band[roll / 10_000 % band.Length].Item;
    }

    // ---- Free wish (function 101) ------------------------------------------------------------------------------------

    /// <summary>NpcFunFood replies. Page 1: remaining*1000+1. Page 2: seconds*100+2 wait, 100 none left, 200 bag
    /// full, 300 success (the en_us text says "Horror Story", zh_cn "希腊美味食谱": a client translation slip),
    /// 400 incorrect operation, 700 outside the event.</summary>
    public const int WishNoneLeft = 100, WishBagFull = 200, WishSuccess = 300, WishIncorrect = 400, WishClosed = 700;

    public static int WishOpening(int usedToday) => Math.Max(0, WishesPerDay - usedToday) * 1000 + 1;

    public static int WishWait(int secondsLeft) => Math.Clamp(secondsLeft, 1, WishGapSeconds) * 100 + 2;

    // ---- Carnival Envoy wish (function 74) ---------------------------------------------------------------------------

    public enum Tier { Rare = 0, Common = 1 }

    /// <summary>One wish: the button, its credit cost (en_us label), prize items (one is picked at random) and count.</summary>
    public readonly record struct Wish(int Button, int Cost, int[] Items, int Count, Tier Tier, bool SecondPage);

    // Recipe items per profession (ProfessionBlueprintCatalog: 1 Alchemy, 2 Smithing, 3 Artificer, 4 Scholar), the 28
    // recipes NF_L0_TZN13xxx names.
    private static readonly int[] AlchemyRecipes = [13009, 13010, 13014, 13016, 13022, 13025, 13032];
    private static readonly int[] SmithingRecipes = [13200, 13205, 13207, 13208, 13211, 13213];
    private static readonly int[] ArtificerRecipes = [13413, 13414, 13420, 13421, 13424, 13425, 13426, 13430, 13433];
    private static readonly int[] ScholarRecipes = [13616, 13617, 13626, 13627, 13640, 13641];

    public static readonly Wish[] Wishes =
    [
        // Page 1 (NpcPermit Index 1): direct prizes, 500 credits each. 11056 Magic Jade: Wing Race; mounts 14260 Nemean
        // Wolf, 14280 African Lion, 14240 Parnitha Boar (BZN121/122/123, ids from NF_L0_TZN*).
        new(120, 500, [11056], 1, Tier.Rare, false),
        new(121, 500, [14260], 1, Tier.Rare, false),
        new(122, 500, [14280], 1, Tier.Rare, false),
        new(123, 500, [14240], 1, Tier.Rare, false),
        // Page 2 after 124: a recipe of the chosen profession, 40 credits. Button order is Alchemy, Scholarship,
        // Blacksmithing, Mythcrafting (BZN210-213).
        new(210, 40, AlchemyRecipes, 1, Tier.Common, true),
        new(211, 40, ScholarRecipes, 1, Tier.Common, true),
        new(212, 40, SmithingRecipes, 1, Tier.Common, true),
        new(213, 40, ArtificerRecipes, 1, Tier.Common, true),
        // Page 2 after 125: level II pet skills, 500 points (ids = NF_L0_TZN keys, zh_cn names match the items; the
        // en_us "Life Totem II" label of 225 is Iceshot II 10299 in zh_cn and in the key).
        new(220, 500, [10241], 1, Tier.Rare, true),
        new(221, 500, [10243], 1, Tier.Rare, true),
        new(222, 500, [10253], 1, Tier.Rare, true),
        new(223, 500, [10277], 1, Tier.Rare, true),
        new(224, 500, [10279], 1, Tier.Rare, true),
        new(225, 500, [10299], 1, Tier.Rare, true),
        // Page 2 after 126: forging materials as the en_us labels name them (zh_cn has level III pet skills here):
        // Level 1 Crystal x25 (30), Level 2 x10 (100), Level 3 x5 (250), Level 4 x3 (300), Quartz Plate 2 (100),
        // Quartz Plate 3 (500).
        new(230, 30, [4230], 25, Tier.Common, true),
        new(231, 100, [4231], 10, Tier.Common, true),
        new(232, 250, [4232], 5, Tier.Common, true),
        new(233, 300, [4233], 3, Tier.Common, true),
        new(234, 100, [9961], 1, Tier.Common, true),
        new(235, 500, [9962], 1, Tier.Rare, true)
    ];

    /// <summary>
    /// Page 1 buttons. 125 (level II pet skills) is left out: NpcPermit.lua draws it at the same spot as 124 (25,215),
    /// so both would be one unreadable button (GENERATED choice; its prizes stay answerable if a click arrives).
    /// </summary>
    public static readonly int[] PageOneButtons = [120, 121, 122, 123, 124, 126];

    public static int[] PageTwoButtons(int category) => category switch
    {
        124 => [210, 211, 212, 213],
        125 => [220, 221, 222, 223, 224, 225],
        126 => [230, 231, 232, 233, 234, 235],
        _ => []
    };

    public static Wish? FindWish(int button) => Wishes.FirstOrDefault(w => w.Button == button) is { Items: not null } w
        ? w
        : null;

    // NpcPermit replies: credits*1000+5 (page 1 header) / +6 (page 2 header) / +8 (Index 2 remaining) / +7 (Index 3
    // remaining). Index 2 results: 925 came true, 924 did not, 922 not enough credits, 941 bag full, 923 data error.
    // Index 3: 926 / 927 / 930 / 940 / 933. 919 = the event has closed.
    public const int Closed = 919;

    public static (int Success, int Fail, int NoCredits, int BagFull, int Error, int RemainingSuffix) Replies(bool second) =>
        second ? (926, 927, 930, 940, 933, 7) : (925, 924, 922, 941, 923, 8);

    public static int CreditsLine(int credits, int suffix) => Math.Clamp(credits, 0, MaxCredits) * 1000 + suffix;

    /// <summary>Success chance in hundredths of a percent for a tier (GENERATED rates).</summary>
    public static int Odds(Tier tier) => Math.Clamp(Rate(tier == Tier.Rare ? RarePercentKey : CommonPercentKey), 0, 100) * 100;

    /// <summary>Credits a mall purchase earns: <paramref name="goldSpent"/> x rate / 100, rounded down (GENERATED).</summary>
    public static int CreditsFor(long goldSpent) =>
        (int)Math.Clamp(goldSpent * Rate(CreditsPer100GoldKey) / 100L, 0L, MaxCredits);

    /// <summary>The stored balance lives in public.finalise2_npc_daily under this fixed day (it never resets: 2.25.000
    /// "wish-making event closed (credits still usable)").</summary>
    public static readonly DateOnly CreditsDay = new(2000, 1, 1);
    public const string CreditsCounter = "carnival_credits";

    // ---- Drops -------------------------------------------------------------------------------------------------------

    /// <summary>
    /// A Greek Cuisine Recipe from any monster on any map while the Oriental Cuisine season is open (GENERATED rate;
    /// the Artemis Treasure Chest monsters of 2010 do not exist in this client). Own salt, independent of the other
    /// seasons' rolls.
    /// </summary>
    public static IEnumerable<(uint ItemId, int Index)> Drops(short mapId, Guid deathEventId, DateTimeOffset at)
    {
        if (deathEventId == Guid.Empty || !OrientalOpen(at)) return [];
        return Roll(deathEventId, Rate(RecipePercentKey));
    }

    /// <summary>The roll alone (no season or switch check), for the checks.</summary>
    internal static List<(uint ItemId, int Index)> Roll(Guid deathEventId, int recipePercent) =>
        Hit(deathEventId, 0x0C1E0031u, recipePercent) ? [(GreekCuisineRecipe, 0)] : [];

    internal static int Mix(Guid id, uint salt)
    {
        Span<byte> bytes = stackalloc byte[16];
        id.TryWriteBytes(bytes);
        var h = salt;
        for (var i = 0; i < 16; i += 4)
            h = unchecked((h ^ BitConverter.ToUInt32(bytes[i..(i + 4)])) * 2_654_435_761u);
        h ^= h >> 15;
        h = unchecked(h * 2_246_822_519u);
        h ^= h >> 13;
        return (int)(h & 0x7FFF_FFFF);
    }

    private static bool Hit(Guid deathEventId, uint salt, int percent) =>
        percent > 0 && Mix(deathEventId, salt) % 10_000 < Math.Clamp(percent, 0, 100) * 100;

    private static int Rate(string key) => Math.Max(0, (int)ServerRates.Current.Get(key));

    // ---- Rate definitions --------------------------------------------------------------------------------------------

    public const string Category = "Seasonal events (on/off and dates: Events & notices page)";

    private static ServerRateDefinition Absolute(string key, string label, string unit, decimal value, decimal max,
        int order, string text) =>
        new(key, Category, label, unit, ServerRateKind.Absolute, value, 0m, max, 1m, order,
            "Application/Seasons/OrientalCarnivalSeason.cs", text);

    public static IReadOnlyList<ServerRateDefinition> Definitions { get; } =
    [
        Absolute(OrientalSwitchKey, "Oriental Cuisine: Master, recipe wishes, scrolls and recipe drops", "0/1", 1m, 1m, 50,
            "1 = while the Oriental Cuisine season is open the Oriental Cuisine Master stands in both capitals: 4 Greek " +
            "Cuisine Recipes buy a Mysterious Oriental Scroll (its quest pays Delicate Mooncakes for the Lunar Priest), " +
            "3 free recipe wishes a day, and monsters drop recipes. 0 = none of it (the Master is hidden). Scroll " +
            "quests already taken still pay their mooncakes while this is 1."),
        Absolute(RecipePercentKey, "Oriental Cuisine: Greek Cuisine Recipe drop chance", "%", 4m, 100m, 51,
            "Each monster killed anywhere drops a Greek Cuisine Recipe (3919) this often while the season is open " +
            "(GENERATED; the 2010 Artemis Treasure Chest monsters do not exist)."),
        Absolute(AllScrollPercentKey, "Oriental Cuisine: chance of the 2-mooncake scroll", "%", 10m, 100m, 52,
            "Each scroll exchange gives the 'all players' Mysterious Oriental Scroll VII (2 Delicate Mooncakes) this " +
            "often, otherwise one of the two scrolls of the player's level band (GENERATED)."),
        Absolute(CarnivalSwitchKey, "Carnival: Carnival Envoy wish-making and mall credits", "0/1", 1m, 1m, 53,
            "1 = while the Carnival season is open the Carnival Envoy stands in both capitals and takes wish-making " +
            "credits for mounts, recipes, pet skills and forging materials, and Item Mall purchases (Gold) earn " +
            "credits. 0 = none of it (the envoy is hidden). Credits are kept between seasons."),
        Absolute(CreditsPer100GoldKey, "Carnival: wish-making credits per 100 Gold spent in the Item Mall", "credits",
            10m, 10_000m, 54,
            "Credits = Gold spent x this / 100, rounded down, for every Item Mall purchase paid in Gold while the " +
            "season is open (GENERATED: the envoy text says 'selected items'; every Gold item counts here)."),
        Absolute(RarePercentKey, "Carnival: wish odds for mounts, Wing Race jade, pet skills, Quartz Plate 3", "%",
            25m, 100m, 55,
            "Chance a 500-credit wish comes true. The credits are spent either way (GENERATED; 'certain odds')."),
        Absolute(CommonPercentKey, "Carnival: wish odds for recipes, crystals, Quartz Plate 2", "%", 50m, 100m, 56,
            "Chance a recipe or forging-material wish comes true. The credits are spent either way (GENERATED).")
    ];
}
