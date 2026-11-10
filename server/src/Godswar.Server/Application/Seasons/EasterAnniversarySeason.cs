using Godswar.Server.Application.Rates;
using Godswar.Server.Domain.World.Content;

namespace Godswar.Server.Application.Seasons;

/// <summary>
/// W3 seasons: Easter and the IGG Anniversary (the admin F-26 "build the seasons", F-134 "each runs for the 10 days
/// before the event day"). Kept apart from <see cref="SeasonalEvents"/> so the sibling season branches merge cleanly.
/// Everything here needs the season open in the editor (public.seasonal_events) AND its hot switch at 1
/// (season.easter / season.igg_anniversary, default 1). The same predicate hides the envoys at map entry and gates
/// their menus, so an envoy never stands without working.
/// <list type="bullet">
/// <item>Easter Envoy (Athens_128 / Sparta_128) = function 99 NpcFunRevive.lua ("主城复活节活动", main-city Easter):
/// its REVIVE100T is the NPC's own description, and REVIVE1001-1007 are the Easter Egg -> Exp Pill exchange the
/// 2011 news gives the Easter Envoy. It was advertised as 51 before.</item>
/// <item>Easter Bunny Envoy (Athens_117 / Sparta_117) = function 51 NpcFunRelive.lua: NF_L0_RL_901 is the NPC's
/// description word for word (bunnies, eggs, prizes, Weak Bunny, Lvl 20+). It was not routed before; Athens_117 is
/// not in the published Athens roster and is added here with the capture's appearance (0x111; id GENERATED, see Athens117Ids)
/// at a GENERATED spot (BunnyEnvoyX/Z, moved off the capture's 64,-152 so its pick box clears the IGG envoy's).</item>
/// <item>IGG Anniversary Envoy (Athens_119 / Sparta_119) = function 104 NpcFunIgg.lua: one button, an Anniversary
/// Gold Sack (3989) for 1,000 Gold (2011 news); each click buys one, no confirm (see IsAnniversaryPurchase). Key Pieces (3995, "submit the pieces to the IGG Anniversary Envoy")
/// have no slot in that script: GENERATED, a handful of pieces pay for the sack instead of the Gold.</item>
/// </list>
/// </summary>
internal static class EasterAnniversarySeason
{
    public const string EasterKey = "easter";
    public const string AnniversaryKey = "igg_anniversary";

    public const int EasterEnvoyFunction = 99; // NPC_FLAG_SYS_REVIVE
    public const int BunnyEnvoyFunction = 51; // NPC_FLAG_SYS_RELIVE
    public const int AnniversaryEnvoyFunction = 104; // NPC_FLAG_SYS_IGG

    // Items (client EquipName / EquipDescription).
    public const int EasterEggs = 3985; // "Easter Eggs": Overlap 1
    public const int EasterEgg = 9009; // "Easter Egg": Overlap 99, "exchanged for rewards at the Easter Envoy"
    public const int StrangeEasterEgg = 9008; // "special rewards at the Easter Envoy": GENERATED Easter Token
    public const int ExpPill = 4174;
    public const int BunnyCage = 10087;
    public const int EasterBunnyEgg = 10154; // the Easter Bunny pet: GENERATED "Weak Bunny"
    public const int EasterChocolate = 4514, EasterBread = 4008, EasterLamb = 4036; // GENERATED Easter Package
    public const int AnniversaryGoldSack = 3989;
    public const int KeyPieces = 3995; // Overlap 1

    // Hot switches and GENERATED amounts (server_rates; editor "Server rates" tab).
    public const string EasterSwitchKey = "season.easter";
    public const string EasterAutoDatesKey = "season.easter_auto_dates";
    public const string EasterEggsPercentKey = "season.easter_eggs_percent";
    public const string EasterEggPercentKey = "season.easter_egg_percent";
    public const string StrangeEggPercentKey = "season.easter_strange_egg_percent";
    public const string PackageBunnyPercentKey = "season.easter_package_bunny_percent";
    public const string BunnyExpPerLevelKey = "season.easter_bunny_exp_per_level";
    public const string BunnyTalentKey = "season.easter_bunny_talent";
    public const string BunnyPetExpPerLevelKey = "season.easter_bunny_pet_exp_per_level";
    public const string BunnyZodiacKey = "season.easter_bunny_zodiac";
    public const string AnniversarySwitchKey = "season.igg_anniversary";
    public const string SackGoldKey = "season.igg_sack_gold";
    public const string SackKeyPiecesKey = "season.igg_sack_key_pieces";
    public const string KeyPiecePercentKey = "season.igg_key_piece_percent";

    public const int BunnyDailyExchanges = 40; // NF_L0_RL_903 / 911
    public const int BunnyCagesPerClaim = 40; // NF_L0_RL_912 "You have claimed 40 Bunny Cages"

    public static bool EasterSwitchOn => Rate(EasterSwitchKey) >= 1;
    public static bool AnniversarySwitchOn => Rate(AnniversarySwitchKey) >= 1;

    /// <summary>Easter: editor season open, switch on, and (auto dates on) inside the 10 days up to Easter Sunday.</summary>
    public static bool EasterOpen(DateTimeOffset at) =>
        EasterSwitchOn && SeasonalEvents.IsOpen(EasterKey, at) && (Rate(EasterAutoDatesKey) < 1 || InEasterWindow(at));

    public static bool AnniversaryOpen(DateTimeOffset at) => AnniversarySwitchOn && SeasonalEvents.IsOpen(AnniversaryKey, at);

    public static bool IsEasterEnvoy(string key) => key is "Athens_128" or "Sparta_128";
    public static bool IsBunnyEnvoy(string key) => key is "Athens_117" or "Sparta_117";
    public static bool IsAnniversaryEnvoy(string key) => key is "Athens_119" or "Sparta_119";

    /// <summary>
    /// The IGG envoy's purchase click. The stock client (1.4.4/1.4.5 NpcFun/NpcFun.lua, loaded by NpcFunLoad.xml)
    /// has no NPC_FLAG_SYS_IGG branch in Set_MessageBox, so NpcFunIgg_SetMsg (igg101t + HaveMessageBox) is dead
    /// code and no confirm box ever opens: the function button "Try my luck(1000Gold)" and page 100's
    /// "*Sure, let's go again!" each arrive as sub -1 with no path (rig 2026-11-09), and each is the purchase.
    /// 100 is accepted too (a client that sends the page number). Anything else is not a purchase.
    /// </summary>
    public static bool IsAnniversaryPurchase(int subId, IReadOnlyList<int> path) =>
        (path.Count == 0 && subId <= 0) || subId == AnniversaryObtained || path.Contains(AnniversaryObtained);

    /// <summary>NpcFunIgg.lua's Index 1 pages: 100 igg100t "obtained, more?" (+ go-again button), 197 igg197t
    /// event closed, 198 igg198t not enough gold, 199 NF_L0_MOON200 not enough empty space.</summary>
    public const int AnniversaryObtained = 100, AnniversaryClosed = 197, AnniversaryNoGold = 198, AnniversaryBagFull = 199;

    /// <summary>Easter Sunday (Gregorian, anonymous algorithm).</summary>
    public static DateOnly EasterSunday(int year)
    {
        int a = year % 19, b = year / 100, c = year % 100, d = b / 4, e = b % 4, f = (b + 8) / 25, g = (b - f + 1) / 3;
        int h = (19 * a + b - d - g + 15) % 30, i = c / 4, k = c % 4, l = (32 + 2 * e + 2 * i - h - k) % 7;
        int m = (a + 11 * h + 22 * l) / 451, month = (h + l - 7 * m + 114) / 31, day = (h + l - 7 * m + 114) % 31 + 1;
        return new DateOnly(year, month, day);
    }

    /// <summary>F-134: the 10 days before Easter Sunday plus the day itself, UTC ([Sunday - 10 d, Sunday + 1 d)).</summary>
    public static bool InEasterWindow(DateTimeOffset at)
    {
        var sunday = EasterSunday(at.UtcDateTime.Year).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        return at.UtcDateTime >= sunday.AddDays(-10) && at.UtcDateTime < sunday.AddDays(1);
    }

    /// <summary>
    /// Object ids for Athens_117, first free wins. The capture's own 5255 is the published Athens_116's interaction
    /// id (NpcActorPlacementCatalog keeps the emulator's ids), so it is never free. GENERATED: 5256 (lead's pick),
    /// then 5760, used by nothing else (capture, actor catalog, event NPCs 5700-5733, Christmas 5740/5741, Halloween/May Day 5750-5753). The id is
    /// a runtime handle; the appearance word 0x111 and the template are the capture's.
    /// </summary>
    internal static readonly uint[] Athens117Ids = [5256u, 5760u];

    /// <summary>
    /// Where the Easter Bunny Envoy stands in both capitals (Athens_117 placed here, the published Sparta_117 moved
    /// here). GENERATED: the capture's 64,-152 sits one 4-unit diagonal from the IGG Anniversary Envoy (68,-156), and
    /// on the real client the bunny is drawn just above her, so from 67,-157 clicks on the bunny's body and feet hit
    /// the IGG envoy's pick box instead (rig, after #102). 60,-148 is twice as far from her (about 11.3 units),
    /// 5 from Athens_091 / Sparta_091 (57,-152) and next to 62,-149, where the rig player stood and clicked the bunny
    /// fine. No capital nav grid exists; the walkability evidence is that rig position.
    /// </summary>
    public const float BunnyEnvoyX = 60f, BunnyEnvoyZ = -148f;

    /// <summary>
    /// Map entry, after <see cref="SeasonalEvents.Apply"/>: hides the Easter / Anniversary envoys while this file's
    /// predicate is closed (switch 0, or outside the Easter auto window), places Athens_117 (no map publishes it) with the
    /// capture's template and appearance at BunnyEnvoyX/Z while Easter is open, and moves the published Sparta_117 there. Placement only: no frame is sent here.
    /// </summary>
    public static List<NpcSpawnDefinition> ApplyRoster(short mapId, List<NpcSpawnDefinition> roster, DateTimeOffset at)
    {
        var easter = EasterOpen(at);
        var anniversary = AnniversaryOpen(at);
        var result = roster.Where(npc =>
            !((IsEasterEnvoy(npc.NpcKey) || IsBunnyEnvoy(npc.NpcKey)) && !easter) &&
            !(IsAnniversaryEnvoy(npc.NpcKey) && !anniversary))
            .Select(static npc => npc.NpcKey == "Sparta_117" ? npc with { X = BunnyEnvoyX, Z = BunnyEnvoyZ } : npc)
            .ToList();
        if (mapId != 1 || !easter || result.Any(static npc => npc.NpcKey == "Athens_117") ||
            !Infrastructure.WorldContent.CapturedNpcPlacements.TryFind("Athens_117", out var placed))
            return result;
        var taken = result.SelectMany(static npc => new[] { npc.ObjectId, npc.InteractionId }).ToHashSet();
        var id = Athens117Ids.FirstOrDefault(candidate => !taken.Contains(candidate));
        if (id == 0)
        {
            Console.WriteLine($"[season] Athens_117 not placed: ids {string.Join('/', Athens117Ids)} are taken on map 1");
            return result;
        }

        result.Add(new NpcSpawnDefinition(1, "Athens", "Athens_117", placed.TemplateKey, id, BunnyEnvoyX,
            BunnyEnvoyZ, id, placed.AppearanceType, placed.Facing, [], []));
        return result;
    }

    // ---- Easter Envoy (99): Easter Egg -> Exp Pills, REVIVE1001-1007 ------------------------------------------------

    /// <summary>
    /// The k-th egg exchange of the day (k from 1): costs k eggs and pays 35 - 5k Exp Pills for k = 1..6 (REVIVE1001-
    /// 1006: 1 -> 30 ... 6 -> 5); from the 7th, 1 egg -> 1 pill (REVIVE1007, which names no count: GENERATED).
    /// Returns (eggs, pills, reply).
    /// </summary>
    public static (int Eggs, int Pills, int Reply) EggExchange(int exchangeNumber)
    {
        var k = Math.Max(1, exchangeNumber);
        return k <= 6 ? (k, 35 - 5 * k, 1000 + k) : (1, 1, 1007);
    }

    /// <summary>The GENERATED Easter Package (no package item exists): its contents, granted directly.</summary>
    public static (int ItemId, int Count)[] PackageContents(bool bunny) => bunny
        ? [(EasterChocolate, 1), (EasterBread, 5), (EasterLamb, 5), (EasterBunnyEgg, 1)]
        : [(EasterChocolate, 1), (EasterBread, 5), (EasterLamb, 5)];

    // ---- Easter Bunny Envoy (51): egg turn-in, rotating prizes -------------------------------------------------------

    public enum BunnyPrize { Experience = 0, TalentPoints = 1, PetExperience = 2, ZodiacEnergy = 3 }

    /// <summary>
    /// NF_L0_RL_903: "prizes are rotated among 4 types - Exp, Talent Points, Pet Exp and Zodiac energy. Plus, bigger
    /// and better prizes are expected every time". The k-th turn-in of the day gets type (k-1) mod 4 and the base
    /// amount x (1 + (k-1)/4), i.e. every full round of four pays one base more (GENERATED).
    /// </summary>
    public static (BunnyPrize Prize, long Amount) BunnyReward(int exchangeNumber, int level)
    {
        var k = Math.Max(1, exchangeNumber);
        var prize = (BunnyPrize)((k - 1) % 4);
        var multiplier = 1 + (k - 1) / 4;
        long baseAmount = prize switch
        {
            BunnyPrize.Experience => (long)Rate(BunnyExpPerLevelKey) * Math.Max(1, level),
            BunnyPrize.TalentPoints => Rate(BunnyTalentKey),
            BunnyPrize.PetExperience => (long)Rate(BunnyPetExpPerLevelKey) * Math.Max(1, level),
            _ => Rate(BunnyZodiacKey)
        };
        return (prize, Math.Min(100_000_000L, baseAmount * multiplier));
    }

    // ---- Drops ------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Season drops (GENERATED rates). Easter, any monster on any map: Easter Eggs (3985, the Bunny Envoy's), an Easter
    /// Egg (9009, the Easter Envoy's) and a Strange Easter Egg (9008, the GENERATED Easter Token). IGG Anniversary:
    /// Key Pieces (3995) from monsters on Marathon (11) and Peloponnese (13), the Key Collectors' maps. Rolled from
    /// the death id mixed with a per-item salt, so the Thanksgiving / Christmas / Mid-autumn rolls stay independent.
    /// </summary>
    public static IEnumerable<(uint ItemId, int Index)> Drops(short mapId, Guid deathEventId, DateTimeOffset at)
    {
        if (deathEventId == Guid.Empty) return [];
        var easter = EasterOpen(at);
        var anniversary = AnniversaryOpen(at) && mapId is 11 or 13;
        if (!easter && !anniversary) return [];
        return Roll(deathEventId, easter ? Rate(EasterEggsPercentKey) : 0, easter ? Rate(EasterEggPercentKey) : 0,
            easter ? Rate(StrangeEggPercentKey) : 0, anniversary ? Rate(KeyPiecePercentKey) : 0);
    }

    /// <summary>The roll alone (no season, switch or map check), for the checks.</summary>
    internal static List<(uint ItemId, int Index)> Roll(Guid deathEventId, int eggsPercent, int eggPercent,
        int strangePercent, int keyPercent)
    {
        var list = new List<(uint, int)>(4);
        if (Hit(deathEventId, 0xEA57E201u, eggsPercent)) list.Add((EasterEggs, 0));
        if (Hit(deathEventId, 0xEA57E202u, eggPercent)) list.Add((EasterEgg, 1));
        if (Hit(deathEventId, 0xEA57E203u, strangePercent)) list.Add((StrangeEasterEgg, 2));
        if (Hit(deathEventId, 0x16610004u, keyPercent)) list.Add((KeyPieces, 3));
        return list;
    }

    private static bool Hit(Guid deathEventId, uint salt, int percent)
    {
        if (percent <= 0) return false;
        Span<byte> bytes = stackalloc byte[16];
        deathEventId.TryWriteBytes(bytes);
        var h = salt;
        for (var i = 0; i < 16; i += 4)
            h = unchecked((h ^ BitConverter.ToUInt32(bytes[i..(i + 4)])) * 2_654_435_761u);
        h ^= h >> 15;
        h = unchecked(h * 2_246_822_519u);
        h ^= h >> 13;
        return h % 10_000 < Math.Clamp(percent, 0, 100) * 100;
    }

    private static int Rate(string key) => Math.Max(0, (int)ServerRates.Current.Get(key));

    // ---- Rate definitions -------------------------------------------------------------------------------------------

    public const string Category = "Seasonal events (on/off and dates: Events & notices page)";

    private static ServerRateDefinition Absolute(string key, string label, string unit, decimal value, decimal max,
        int order, string text) =>
        new(key, Category, label, unit, ServerRateKind.Absolute, value, 0m, max, 1m, order,
            "Application/Seasons/EasterAnniversarySeason.cs", text);

    public static IReadOnlyList<ServerRateDefinition> Definitions { get; } =
    [
        Absolute(EasterSwitchKey, "Easter: envoys, egg exchanges and egg drops", "0/1", 1m, 1m, 30,
            "1 = while the Easter season is open, the Easter Envoy and Easter Bunny Envoy stand in both capitals and " +
            "take eggs, and monsters drop eggs. 0 = none of it (the envoys are hidden)."),
        Absolute(EasterAutoDatesKey, "Easter: only the 10 days before Easter Sunday", "0/1", 1m, 1m, 31,
            "1 = Easter is open only from 10 days before Easter Sunday to the end of Easter Sunday (UTC), worked out " +
            "for each year, on top of the season being on (the admin F-134; Easter moves every year). 0 = the editor " +
            "dates alone."),
        Absolute(EasterEggsPercentKey, "Easter: 'Easter Eggs' drop chance (Bunny Envoy)", "%", 3m, 100m, 32,
            "Each monster killed anywhere drops Easter Eggs (3985) this often (GENERATED)."),
        Absolute(EasterEggPercentKey, "Easter: 'Easter Egg' drop chance (Easter Envoy)", "%", 3m, 100m, 33,
            "Each monster killed anywhere drops an Easter Egg (9009, Exp Pill exchange) this often (GENERATED)."),
        Absolute(StrangeEggPercentKey, "Easter: Strange Easter Egg drop chance", "%", 1m, 100m, 34,
            "Strange Easter Egg (9008): the Easter Envoy's daily Easter Package costs one (GENERATED)."),
        Absolute(PackageBunnyPercentKey, "Easter: Easter Bunny Egg chance in the Easter Package", "%", 5m, 100m, 35,
            "The Easter Package is 1 Easter Chocolate, 5 Easter Bread and 5 Easter Lamb, plus this chance of an Easter " +
            "Bunny Egg pet (GENERATED)."),
        Absolute(BunnyExpPerLevelKey, "Easter: Bunny Envoy EXP per level", "EXP", 500m, 1_000_000m, 36,
            "EXP prize = this x character level, x (1 + turn-ins today / 4) (GENERATED). Events EXP rate on top."),
        Absolute(BunnyTalentKey, "Easter: Bunny Envoy Talent Points", "TP", 5m, 10_000m, 37,
            "Talent Point prize, x (1 + turn-ins today / 4) (GENERATED)."),
        Absolute(BunnyPetExpPerLevelKey, "Easter: Bunny Envoy pet EXP per level", "EXP", 1_000m, 1_000_000m, 38,
            "Pet EXP prize = this x character level to the summoned pet (GENERATED). No pet out = EXP instead."),
        Absolute(BunnyZodiacKey, "Easter: Bunny Envoy Zodiac energy", "energy", 10m, 10_000m, 39,
            "Zodiac energy prize, up to the storage limit (GENERATED). A full store = EXP instead."),
        Absolute(AnniversarySwitchKey, "IGG Anniversary: envoy, Gold Sacks and Key Piece drops", "0/1", 1m, 1m, 40,
            "1 = while the IGG Anniversary season is open the IGG Anniversary Envoy sells Anniversary Gold Sacks and " +
            "monsters on Marathon / Peloponnese drop Key Pieces. 0 = none of it (the envoy is hidden)."),
        Absolute(SackGoldKey, "IGG Anniversary: Gold Sack price", "Gold", 1_000m, 1_000_000m, 41,
            "Gold for one Anniversary Gold Sack (1,000 in the 2011 news and the client's text)."),
        Absolute(SackKeyPiecesKey, "IGG Anniversary: Key Pieces that pay for a sack", "pieces", 5m, 50m, 42,
            "Holding this many Key Pieces, a sack takes them instead of the Gold (GENERATED; each piece fills a bag " +
            "slot). 0 = Gold only."),
        Absolute(KeyPiecePercentKey, "IGG Anniversary: Key Piece drop chance", "%", 3m, 100m, 43,
            "Monsters on Marathon and Peloponnese (the Key Collectors' maps) drop Key Pieces this often (GENERATED).")
    ];
}
