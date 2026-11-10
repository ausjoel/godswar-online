using Godswar.Server.Application.Finalise2;
using Godswar.Server.Application.Rates;

namespace Godswar.Server.Application.Seasons;

// ROUND3 W3 Spring Festival (the admin F-26 / F-134): the pure rules of the "spring_festival" season, kept out of
// SeasonalEvents.cs so the other seasons' PRs merge cleanly. The envoys (Athens/Sparta_092, _112) are already gated
// by SeasonalEvents.EnvoySeason and are captured NPCs, so nothing is added to the map roster.

/// <summary>One Spring Festival Envoy exchange (page-2 button of NpcFunNewYear.lua) and what it takes.</summary>
internal readonly record struct SpringExchange(int Button, (int ItemId, int Count)[] Takes, int DailyCap);

/// <summary>
/// W3 Spring Festival. Sources: the client scripts NpcFunNewYear.lua (function 48, Spring Festival Envoy) and
/// NpcFunFire.lua (function 89, Lunar Beast Slayer), their LuaText (NF_NY_*, CJ_FIRE*), the item texts, and IGG's
/// 2010 / 2011 Spring Festival posts (GodsWar-2.51/Official-Site/NEWS-EVENTS.md s.2.1: the 2010 exchange table with
/// level bands, the 2011 Lunar Beast rules). Everything else is GENERATED and marked so.
/// </summary>
internal static class SpringFestivalRules
{
    public const string Season = "spring_festival";
    public const int EnvoyFunction = 48; // NPC_FLAG_SYS_NewYear
    public const int SlayerFunction = 89; // NPC_FLAG_SYS_FIRE

    // Items (client EquipName.dat / item_templates).
    public const int GodsPlate = 3963, WarPlate = 3964, OnlinePlate = 3965, GamePlate = 3966;
    public const int GreetingsCard = 3967, PreservedHam = 4006, Chocolate = 4007, RiceCake = 4034, PerfectRose = 4035;
    public const int Firecracker = 14041, ExpPill = 4174;
    public const int Crystal1 = 4230, Crystal2 = 4231, Crystal3 = 4232, Crystal4 = 4233;
    public const int SimpleFirework = 3939, AdvFirework = 3940, AdvFireworkOfLove = 3942, FireworkOfLove = 3949;
    public const int FlyingCarpet = 14340, YoungYetiJade = 11065;

    public static readonly int[] Plates = [GodsPlate, WarPlate, OnlinePlate, GamePlate];

    // ---- Spring Festival Envoy (function 48) ------------------------------------------------------------------
    // Page 1 = the six topics, page 2 = the topic's buttons, page 3 = a result (302-309) or the plate form (300),
    // page 4 = the form's result (400-403). The client's page counter advances once per reply, so every reply below
    // carries only numbers of the page it lands on.
    public static readonly int[] OpeningMenu = [100, 101, 102, 103, 104, 105];

    public static int[]? TopicPage(int click) => click switch
    {
        100 => [200, 201, 202],
        101 => [203, 204],
        102 => [205, 206],
        103 => [207, 208],
        104 => [209, 210],
        105 => [211, 212],
        _ => null
    };

    // Page-3 results (NF_NY_T302-T309) and page-4 results after the plate form (T400 and its 401-403 aliases).
    public const int NoItems = 302, NotCouple = 303, WrongGender = 304, Tomorrow = 305, AlreadyMade = 306,
        Exchanged = 307, BagFull = 309, PlateForm = 300;
    public const int FormEmpty = 400, FormNoItems = 401, FormTomorrow = 402, FormExchanged = 403;

    /// <summary>
    /// The page-2 exchanges. Counts and caps from the client buttons and NF_NY_T20-T26 ("up to 5 times a day") and
    /// the 2010 table (single plates 50 a day). The couple exchanges are once per character per season.
    /// </summary>
    public static readonly IReadOnlyDictionary<int, SpringExchange> Exchanges = new Dictionary<int, SpringExchange>
    {
        [200] = new(200, [], 50), // one plate, through the page-3 form
        [201] = new(201, [(GodsPlate, 1), (WarPlate, 1), (OnlinePlate, 1), (GamePlate, 1)], 5),
        [202] = new(202, [(GodsPlate, 1), (WarPlate, 1), (OnlinePlate, 1), (GamePlate, 1), (GreetingsCard, 1)], 5),
        [203] = new(203, [(PreservedHam, 10)], 5), // 300 Honor
        [204] = new(204, [(PreservedHam, 10)], 5), // 200 HardPoints
        [205] = new(205, [(RiceCake, 10)], 5), // 200 Stored Energy
        [206] = new(206, [(RiceCake, 10)], 5), // Level 2 Crystal
        [207] = new(207, [(Chocolate, 10)], 5), // Simple Firework
        [208] = new(208, [(Chocolate, 20)], 5), // Adv. Firework
        [209] = new(209, [(PerfectRose, 10)], 5), // Adv. Firework of Love
        [210] = new(210, [(PerfectRose, 20)], 5), // Firework of Love
        [211] = new(211, [(PerfectRose, 9), (GreetingsCard, 1)], 1), // male only
        [212] = new(212, [(Chocolate, 9), (GreetingsCard, 1)], 1) // female only
    };

    public const int HamHonor = 300, HamHardPoints = 200, RiceZodiacEnergy = 200;
    public const int SetCardPetExperience = 500_000; // 2010 post: "+ 500,000 pet EXP to the summoned pet"

    /// <summary>The item an exchange gives (fireworks and the crystal), or 0.</summary>
    public static int ItemReward(int button) => button switch
    {
        206 => Crystal2,
        207 => SimpleFirework,
        208 => AdvFirework,
        209 => AdvFireworkOfLove,
        210 => FireworkOfLove,
        _ => 0
    };

    /// <summary>Gender the couple exchange needs: 211 male (1), 212 female (0); -1 = any.</summary>
    public static int RequiredGender(int button) => button switch { 211 => 1, 212 => 0, _ => -1 };

    // 2010 table, bands Lv1-20 / 21-40 / 41-60 / 61-80 / 81-100 / 101-110 / 111-120. Lv121+ keep the last band
    // (GENERATED: the 2010 cap was 120).
    private static readonly int[] BandTops = [20, 40, 60, 80, 100, 110, int.MaxValue];
    private static readonly (int Exp, int Talent)[] SinglePlate =
        [(500, 2), (1_000, 2), (2_000, 3), (4_000, 3), (8_000, 4), (10_000, 4), (15_000, 5)];
    private static readonly (int Exp, int Talent)[] PlateSet =
        [(3_000, 10), (6_000, 10), (10_000, 15), (30_000, 15), (100_000, 20), (250_000, 20), (300_000, 25)];

    public static int Band(int level) => Array.FindIndex(BandTops, top => Math.Max(1, level) <= top);

    /// <summary>EXP and Talent Points for a plate exchange (200 single, 201/202 set) at a level.</summary>
    public static (int Experience, int TalentPoints) PlateReward(int button, int level) =>
        button == 200 ? SinglePlate[Band(level)] : PlateSet[Band(level)];

    /// <summary>
    /// The couple exchange (211/212): GENERATED. The client promises "lots of EXP and Talent Points"; this server
    /// pays the plate-set reward of the player's band. The partner's copy and the team check are not built.
    /// </summary>
    public static (int Experience, int TalentPoints) CoupleReward(int level) => PlateSet[Band(level)];

    public static string DailyCounter(int button) => $"spring_{button}";

    public const string CoupleCounter = "spring_couple";

    // ---- Lunar Beast Slayer (function 89) ---------------------------------------------------------------------
    public const int SlayerNoFirecracker = 999, SlayerBagFull = 998, SlayerDailyLimit = 997;
    public const int FirecrackersPerDay = 10; // CJ_FIRET997
    public const int FreeSlotsNeeded = 2; // NF_L0_MOON903

    /// <summary>
    /// The lucky find of one firecracker (CJ_FIRET101-107), from a roll 0-9999. Weights GENERATED (IGG: "a chance of
    /// a Flying Carpet or Magic Jade: Young Yeti"): L1 x10 10 %, L2 5 %, L3 2 %, L4 0.5 %, carpet 0.1 %, Yeti 0.4 %,
    /// otherwise 107 (no find).
    /// </summary>
    public static (int Result, int ItemId, int Count) Bonus(int roll) => roll switch
    {
        < 1_000 => (101, Crystal1, 10),
        < 1_500 => (102, Crystal2, 1),
        < 1_700 => (103, Crystal3, 1),
        < 1_750 => (104, Crystal4, 1),
        < 1_760 => (105, FlyingCarpet, 1),
        < 1_800 => (106, YoungYetiJade, 1),
        _ => (107, 0, 0)
    };

    /// <summary>The count line the script draws from n*1000+9 ("... n everyone's been lighting ...").</summary>
    public static int CountLine(int litThisSeason) => checked(Math.Max(0, litThisSeason) * 1000 + 9);

    /// <summary>
    /// The Lunar Beast: no monster of that name exists in the client (Monster.ini), so it is not spawned. GENERATED
    /// stand-in for the 2011 "250 firecrackers summon the Lunar Beast": whoever lights every 250th firecracker of the
    /// season drives it off and gets a bound Flying Carpet, announced to everyone.
    /// </summary>
    public const int LunarBeastEvery = 250;

    public static bool DrivesOffLunarBeast(int litThisSeason) => litThisSeason > 0 && litThisSeason % LunarBeastEvery == 0;

    /// <summary>The server-wide counter's key in finalise2_npc_daily (character 0, 1 January of the season year).</summary>
    public const string LitCounter = "spring_firecrackers_lit";

    /// <summary>The per-character daily counter (10 a day).</summary>
    public const string DailyFirecrackers = "spring_firecrackers";

    public static DateOnly SeasonDay(DateTimeOffset at) => new(at.UtcDateTime.Year, 1, 1);

    // ---- Season drops ----------------------------------------------------------------------------------------
    /// <summary>
    /// The Snow Demons / Spring Yetis are not in the client's monster catalog, so while the season is open every
    /// monster in the suburbs (Athens_Newbie map 2, Sparta_Newbie map 4) carries their goods (GENERATED, as the
    /// Thanksgiving turkeys): season.spring_goods_percent of kills drop one of the 2010 list (each plate 10 %, card 10,
    /// ham 15, rice cake 15, chocolate 10, rose 10), and season.spring_firecracker_percent drop a firecracker (2011: the
    /// Merrymaker's swap, which has no client script). Rolled from bytes of the death id not used by other seasons.
    /// </summary>
    public static IEnumerable<(uint ItemId, int Index)> Drops(short mapId, Guid deathEventId, DateTimeOffset at,
        int goodsPercent, int firecrackerPercent)
    {
        if (mapId is not (2 or 4) || deathEventId == Guid.Empty || !SeasonalEvents.IsOpen(Season, at)) yield break;
        var bytes = deathEventId.ToByteArray();
        var goods = BitConverter.ToUInt16(bytes, 0) % 10_000;
        var kind = BitConverter.ToUInt16(bytes, 2) % 100;
        var fire = BitConverter.ToUInt16(bytes, 4) % 10_000;
        if (goods < Math.Clamp(goodsPercent, 0, 100) * 100)
            yield return ((uint)Goods(kind), 0);
        if (fire < Math.Clamp(firecrackerPercent, 0, 100) * 100)
            yield return ((uint)Firecracker, 1);
    }

    public static int Goods(int roll) => roll switch
    {
        < 10 => GodsPlate,
        < 20 => WarPlate,
        < 30 => OnlinePlate,
        < 40 => GamePlate,
        < 50 => GreetingsCard,
        < 65 => PreservedHam,
        < 80 => RiceCake,
        < 90 => Chocolate,
        _ => PerfectRose
    };

    // ---- Editor rates ----------------------------------------------------------------------------------------
    public const string GoodsDropPercent = "season.spring_goods_percent"; // GENERATED
    public const string FirecrackerDropPercent = "season.spring_firecracker_percent"; // GENERATED
    public const string ExpPillsPerFirecracker = "season.spring_firecracker_pills"; // client text: 40

    public static IReadOnlyList<ServerRateDefinition> RateDefinitions { get; } =
    [
        new(GoodsDropPercent, Finalise2Rates.SeasonCategory, "Spring Festival: festival goods drop chance", "%",
            ServerRateKind.Absolute, 10m, 0m, 100m, 1m, 20, "Application/Seasons/SpringFestivalRules.cs",
            "While the Spring Festival is open, each monster killed in the suburbs of Athens or Sparta drops one gold " +
            "plate, Greetings Card, Preserved Ham, Rice Cake, Chocolate or Perfect Rose this often (the client has no Snow " +
            "Demon monster; GENERATED). Hand them to the Spring Festival Envoy."),
        new(FirecrackerDropPercent, Finalise2Rates.SeasonCategory, "Spring Festival: firecracker drop chance", "%",
            ServerRateKind.Absolute, 2m, 0m, 100m, 1m, 21, "Application/Seasons/SpringFestivalRules.cs",
            "Chance of a Spring Festival Firecracker from the same monsters (GENERATED: the Spring Merrymaker's swap has no " +
            "client script). The Lunar Beast Slayer takes 10 a day."),
        new(ExpPillsPerFirecracker, Finalise2Rates.SeasonCategory, "Spring Festival: Exp Pills per firecracker", "pills",
            ServerRateKind.Absolute, 40m, 0m, 99m, 1m, 22, "Game/GameClientHandler.SpringFestival.cs",
            "Bound Exp Pills the Lunar Beast Slayer gives for each firecracker. The client's line says 40.")
    ];
}
