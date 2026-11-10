using Godswar.Server.Application.Rates;
using Godswar.Server.Domain.World.Content;

namespace Godswar.Server.Application.Seasons;

/// <summary>
/// W3 seasons: Halloween and May Day (the admin F-26 "build the seasons", F-134 "each runs for the 10 days before the event
/// day"). Kept apart from <see cref="SeasonalEvents"/> so the sibling season branches merge cleanly. Everything here
/// needs the season open in the editor (public.seasonal_events) AND its hot switch at 1 (season.halloween /
/// season.may_day, default 1) AND, while the auto-dates switch is 1 (default), the 10 days before the event day plus
/// the day itself (UTC). The same predicate hides / places the envoys at map entry and gates their menus.
/// <list type="bullet">
/// <item>Halloween Envoy (Athens_053 / Sparta_053) = function 113 NpcFunHallo.lua: claim the Halloween Suit (once per
/// season year), claim the Halloween Wand (once a day, suit worn), and turn 10 Pumpkin Candy (4507) into EXP, Talent
/// Points, pet EXP or Zodiac energy, 8 times a day (NF_L0_Hallo202 / the captured 8001).</item>
/// <item>May Day Envoy (no client NPC: a GENERATED actor at the item text's Athens / Sparta (159,76)) = function 55
/// NpcFunWorkday.lua (五一活动使者): Fertiliser (3987) grows the faction tree, the first tree to grow wins; and function
/// 56 NpcFunWorker.lua (肥料供应商, the Fertiliser Supplier) on the same actor hands out one Fertiliser at a time.</item>
/// </list>
/// The en_us client re-used the 55/56 texts for an IGG key-piece battle ("Submit Key Pieces", "Anniversary Chest"); the
/// zh_cn originals (commented out in LuaText.lua) are the faction tree. The numbers are the same, so the server runs the
/// tree and adds a plain-English chat line with the tree's progress.
/// </summary>
internal static class HalloweenMayDaySeason
{
    public const string HalloweenKey = "halloween";
    public const string MayDayKey = "may_day";

    public const int HalloweenEnvoyFunction = 113; // NPC_FLAG_SYS_HALLO
    public const int MayDayEnvoyFunction = 55; // NPC_FLAG_SYS_WORKDAY 五一活动使者
    public const int FertiliserSupplierFunction = 56; // NPC_FLAG_SYS_WORKER 肥料供应商

    // Items (client EquipName / EquipDescription / ItemBaseAttribute).
    public const int PumpkinCandy = 4507; // Overlap 99; also a 30-minute +10 % EXP consumable
    public const int HalloweenSuit = 8230; // "Halloween Suit(1 days)", Overlap 1
    public static readonly int[] HalloweenSuits = [8230, 8237, 8238]; // 1 day and the two perpetual ones
    public const int HalloweenWand = 10085; // Overlap 99
    public const int WarriorSoul = 3943; // "Monsters will drop warriors souls ... this Halloween" (+88 honor on use)
    public const int Fertiliser = 3987; // Overlap 1
    public const int CrystalGiftPackGreat = 14000; // winners (NF_L0_WKD0101 zh: 水晶大礼包)
    public const int CrystalGiftPackSmall = 14001; // the other faction (水晶小礼包)

    public const int HalloweenMinimumLevel = 20; // NF_L0_Hallo105
    public const int CandyPerExchange = 10; // NF_L0_Hallo5-8
    public const int CandyExchangesPerDay = 8; // NF_L0_Hallo202, the captured 8001
    public const int MayDayMinimumLevel = 30; // NF_L0_WKD230

    // Object ids (GENERATED). 5700-5735 are event NPCs / Thanksgiving, 5740/5741 the Christmas branch's Santa.
    public const uint AthensHalloweenFallbackId = 5750; // only if the captured 5191 cannot be published
    public const uint SpartaHalloweenFallbackId = 5751; // only if the captured 5050 cannot be published
    public const uint AthensMayDayEnvoyId = 5752;
    public const uint SpartaMayDayEnvoyId = 5753;

    public const string AthensMayDayKey = "Athens_MayDay";
    public const string SpartaMayDayKey = "Sparta_MayDay";

    // Hot switches and GENERATED amounts (server_rates; editor "Server rates" tab).
    public const string HalloweenSwitchKey = "season.halloween";
    public const string HalloweenAutoDatesKey = "season.halloween_auto_dates";
    public const string CandyPercentKey = "season.halloween_candy_percent";
    public const string WarriorSoulPercentKey = "season.halloween_soul_percent";
    public const string CandyExpPerLevelKey = "season.halloween_exp_per_level";
    public const string CandyTalentKey = "season.halloween_talent";
    public const string CandyPetExpPerLevelKey = "season.halloween_pet_exp_per_level";
    public const string CandyZodiacKey = "season.halloween_zodiac";
    public const string MayDaySwitchKey = "season.may_day";
    public const string MayDayAutoDatesKey = "season.may_day_auto_dates";
    public const string TreeTargetKey = "season.may_day_tree_target";
    public const string FertiliserDailyCapKey = "season.may_day_daily_cap";
    public const string TreeExpPerLevelKey = "season.may_day_exp_per_level";

    public static bool HalloweenSwitchOn => Rate(HalloweenSwitchKey) >= 1;
    public static bool MayDaySwitchOn => Rate(MayDaySwitchKey) >= 1;

    /// <summary>Halloween: editor season open, switch on, and (auto dates on) inside [Oct 21, Nov 1) UTC.</summary>
    public static bool HalloweenOpen(DateTimeOffset at) =>
        HalloweenSwitchOn && SeasonalEvents.IsOpen(HalloweenKey, at) &&
        (Rate(HalloweenAutoDatesKey) < 1 || InWindow(at, 10, 31));

    /// <summary>May Day: editor season open, switch on, and (auto dates on) inside [Apr 21, May 2) UTC.</summary>
    public static bool MayDayOpen(DateTimeOffset at) =>
        MayDaySwitchOn && SeasonalEvents.IsOpen(MayDayKey, at) && (Rate(MayDayAutoDatesKey) < 1 || InWindow(at, 5, 1));

    /// <summary>F-134: the 10 days before the event day plus the day itself, UTC ([day - 10 d, day + 1 d)).</summary>
    public static bool InWindow(DateTimeOffset at, int month, int day)
    {
        var eventDay = new DateTime(at.UtcDateTime.Year, month, day, 0, 0, 0, DateTimeKind.Utc);
        return at.UtcDateTime >= eventDay.AddDays(-10) && at.UtcDateTime < eventDay.AddDays(1);
    }

    /// <summary>True on the event day itself (UTC): May Day claims open then even if no tree has grown.</summary>
    public static bool IsEventDay(DateTimeOffset at, int month, int day) =>
        at.UtcDateTime.Month == month && at.UtcDateTime.Day == day;

    /// <summary>Neither window crosses New Year, so the season year is the UTC year.</summary>
    public static int SeasonYear(DateTimeOffset at) => at.UtcDateTime.Year;

    public static bool IsHalloweenEnvoy(string key) => key is "Athens_053" or "Sparta_053";
    public static bool IsMayDayEnvoy(string key) => key is AthensMayDayKey or SpartaMayDayKey;

    /// <summary>
    /// Map entry, before <see cref="SeasonalEvents.Apply"/> (which still removes the Halloween Envoy while the editor
    /// season is off). Removes the Halloween Envoy while this file's predicate is closed. While it is open:
    /// <list type="bullet">
    /// <item>the Halloween Envoy is re-added at a free GENERATED id if the published roster lost it. The 2026-11-08
    /// handoff said its id clashed with the local player id 0x1448 = 5192; the capture actually gives Athens_053 5191
    /// and Athens_054 5192 (the rig logs show Athens_054 skipped, never Athens_053), so this is a guard only.</item>
    /// <item>the May Day Envoy is placed in each capital at the Fertiliser text's (159,76) (GENERATED actor).</item>
    /// </list>
    /// Placement only: no frame is sent here.
    /// </summary>
    public static List<NpcSpawnDefinition> ApplyRoster(short mapId, IReadOnlyList<NpcSpawnDefinition> roster,
        DateTimeOffset at)
    {
        var halloween = HalloweenOpen(at);
        var mayDay = MayDayOpen(at);
        var result = roster.Where(npc => halloween || !IsHalloweenEnvoy(npc.NpcKey))
            .Where(npc => !IsMayDayEnvoy(npc.NpcKey)).ToList();
        if (mapId is not (0 or 1)) return result;
        var scene = mapId == 1 ? "Athens" : "Sparta";
        var appearance = mapId == 1 ? 0x111u : 0x11u; // each capital's captured appearance word
        if (halloween)
        {
            var key = scene + "_053";
            if (!result.Any(npc => npc.NpcKey == key) &&
                Infrastructure.WorldContent.CapturedNpcPlacements.TryFind(key, out var placed))
            {
                var id = mapId == 1 ? AthensHalloweenFallbackId : SpartaHalloweenFallbackId;
                TryAdd(result, new NpcSpawnDefinition(mapId, scene, key, placed.TemplateKey, id, placed.X, placed.Z, id,
                    placed.AppearanceType, placed.Facing, [], []));
            }
        }

        if (mayDay)
        {
            // GENERATED: no client NPC is the May Day Envoy, so the actor wears the Festival Envoy (an avatar, not a
            // mesh: the usual pick box) and faces the city like the capitals' seasonal envoys.
            var id = mapId == 1 ? AthensMayDayEnvoyId : SpartaMayDayEnvoyId;
            TryAdd(result, new NpcSpawnDefinition(mapId, scene, mapId == 1 ? AthensMayDayKey : SpartaMayDayKey,
                scene + "_084_FemMale29", id, 159f, 76f, id, appearance, 3f, [], []));
        }

        return result;
    }

    private static void TryAdd(List<NpcSpawnDefinition> roster, NpcSpawnDefinition npc)
    {
        if (roster.Any(other => other.ObjectId == npc.ObjectId || other.InteractionId == npc.ObjectId ||
                                other.NpcKey == npc.NpcKey))
        {
            Console.WriteLine($"[season] {npc.NpcKey} not placed: object {npc.ObjectId} or the key is taken on map {npc.MapId}");
            return;
        }

        roster.Add(npc);
    }

    // ---- Halloween Envoy (113) --------------------------------------------------------------------------------------

    public enum CandyPrize { Experience = 5, TalentPoints = 6, PetExperience = 7, ZodiacEnergy = 8 }

    /// <summary>The page-2 menu after "Exchange": the four prizes and the exchanges left today, or 201 when none.</summary>
    public static int[] ExchangeMenu(int usedToday)
    {
        var left = Math.Max(0, CandyExchangesPerDay - usedToday);
        // The leading 0 is the captured reply's ([0, 5, 6, 7, 8, 8001]); the script draws nothing for it.
        return left == 0 ? [201] : [0, 5, 6, 7, 8, left * 1000 + 1];
    }

    /// <summary>The prize for 10 candies (GENERATED amounts from the rates; EXP / pet EXP scale with level).</summary>
    public static long CandyPrizeAmount(CandyPrize prize, int level) => Math.Min(100_000_000L, prize switch
    {
        CandyPrize.Experience => (long)Rate(CandyExpPerLevelKey) * Math.Max(1, level),
        CandyPrize.TalentPoints => Rate(CandyTalentKey),
        CandyPrize.PetExperience => (long)Rate(CandyPetExpPerLevelKey) * Math.Max(1, level),
        _ => Rate(CandyZodiacKey)
    });

    /// <summary>True when a worn-equipment string ('#'-separated compact entries) has a Halloween Suit on.</summary>
    public static bool WearsHalloweenSuit(string equipment) =>
        (equipment ?? "").Split('#').Any(static entry =>
            entry.Length > 0 && HalloweenSuits.Contains((int)State.CompactItemEntry.Parse(entry).Id));

    // ---- May Day Envoy (55) / Fertiliser Supplier (56) ---------------------------------------------------------------

    /// <summary>
    /// Tree size (GENERATED, season.may_day_tree_target): the Fertiliser a faction's tree needs to grow fully. The
    /// first faction to reach it wins (NF_L0_WKD0101 zh: 阵营之树先成长起来的阵营将获胜).
    /// </summary>
    public static int TreeTarget => Math.Max(1, Rate(TreeTargetKey));

    public static int FertiliserDailyCap => Math.Max(1, Rate(FertiliserDailyCapKey));

    /// <summary>Fertiliser taken by one submission: all held, within today's cap and the tree's room.</summary>
    public static int FertiliserTaken(int held, int submittedToday, int treePoints) =>
        Math.Max(0, Math.Min(held, Math.Min(FertiliserDailyCap - submittedToday, TreeTarget - treePoints)));

    /// <summary>
    /// The winner on the event day when no tree has grown: the faction with more Fertiliser (GENERATED). A tie is a
    /// win for both (-1).
    /// </summary>
    public static int EventDayWinner(int spartaPoints, int athensPoints) =>
        spartaPoints == athensPoints ? -1 : spartaPoints > athensPoints ? 0 : 1;

    /// <summary>
    /// The claim (GENERATED amounts): the Great pack for the winning faction (both on a tie), else the Small pack; and
    /// the "successful planting" EXP (zh 成功植树奖励：丰厚经验奖励) only when the character's own tree grew fully.
    /// </summary>
    public static (int PackId, long Experience) ClaimReward(bool won, bool ownTreeGrown, int level) =>
        (won ? CrystalGiftPackGreat : CrystalGiftPackSmall,
            ownTreeGrown ? Math.Min(100_000_000L, (long)Rate(TreeExpPerLevelKey) * Math.Max(1, level)) : 0L);

    // ---- Drops ------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Halloween drops from any monster (GENERATED rates): Pumpkin Candy (the wand's devils are not built: the client
    /// will not use an item without a target, as the Christmas gloves showed) and a Warrior Soul (its own text). Rolled
    /// from the death id with this file's salts, so the other seasons' rolls stay independent.
    /// </summary>
    public static IEnumerable<(uint ItemId, int Index)> Drops(Guid deathEventId, DateTimeOffset at)
    {
        if (deathEventId == Guid.Empty || !HalloweenOpen(at)) return [];
        return Roll(deathEventId, Rate(CandyPercentKey), Rate(WarriorSoulPercentKey));
    }

    /// <summary>The roll alone (no season or switch check), for the checks.</summary>
    internal static List<(uint ItemId, int Index)> Roll(Guid deathEventId, int candyPercent, int soulPercent)
    {
        var list = new List<(uint, int)>(2);
        if (Hit(deathEventId, 0x4A110301u, candyPercent)) list.Add((PumpkinCandy, 0));
        if (Hit(deathEventId, 0x4A110302u, soulPercent)) list.Add((WarriorSoul, 1));
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

    private const string RateCategory = "Seasonal events (on/off and dates: Events & notices page)";

    private static ServerRateDefinition Absolute(string key, string label, string unit, decimal value, decimal max,
        int order, string text) =>
        new(key, RateCategory, label, unit, ServerRateKind.Absolute, value, 0m, max, 1m, order,
            "Application/Seasons/HalloweenMayDaySeason.cs", text);

    public static IReadOnlyList<ServerRateDefinition> Definitions { get; } =
    [
        Absolute(MayDaySwitchKey, "May Day: envoy, Fertiliser and faction trees", "0/1", 1m, 1m, 60,
            "1 = while the May Day season is open, the May Day Envoy stands in both capitals at (159,76), hands out " +
            "Fertiliser and takes it for the faction tree. 0 = none of it (the envoy is hidden)."),
        Absolute(MayDayAutoDatesKey, "May Day: only the 10 days before May 1", "0/1", 1m, 1m, 61,
            "1 = May Day is open only from 21 April to the end of 1 May (UTC), on top of the season being on (the admin " +
            "F-134). 0 = the editor dates alone."),
        Absolute(TreeTargetKey, "May Day: Fertiliser a faction tree needs", "Fertiliser", 300m, 100_000m, 62,
            "The first faction whose tree gets this much Fertiliser wins; claims open then (GENERATED; about 15 " +
            "players a day for the ten days). On 1 May claims open anyway and the tree with more Fertiliser wins."),
        Absolute(FertiliserDailyCapKey, "May Day: Fertiliser one character may submit a day", "Fertiliser", 20m, 1_000m,
            63, "Above this the envoy answers 'maximum amount of submissions' (GENERATED)."),
        Absolute(TreeExpPerLevelKey, "May Day: tree reward EXP per level", "EXP", 2_000m, 1_000_000m, 64,
            "With the claim, characters whose own faction tree grew fully get this x level EXP (GENERATED). Events EXP " +
            "rate on top. Winners also get a Crystal Gift Pack (Great), the other faction a Crystal Gift Pack (Small)."),
        Absolute(HalloweenSwitchKey, "Halloween: envoy, suit, wand, candy exchange and drops", "0/1", 1m, 1m, 70,
            "1 = while the Halloween season is open, the Halloween Envoy gives the suit and wand and exchanges Pumpkin " +
            "Candy, and monsters drop candy. 0 = none of it (the envoy is hidden)."),
        Absolute(HalloweenAutoDatesKey, "Halloween: only the 10 days before 31 October", "0/1", 1m, 1m, 71,
            "1 = Halloween is open only from 21 October to the end of 31 October (UTC), on top of the season being on " +
            "(the admin F-134). 0 = the editor dates alone."),
        Absolute(CandyPercentKey, "Halloween: Pumpkin Candy drop chance", "%", 4m, 100m, 72,
            "Each monster killed anywhere drops a Pumpkin Candy this often (GENERATED; the wand's Halloween Devils are " +
            "not built). 10 candies make one exchange at the envoy."),
        Absolute(WarriorSoulPercentKey, "Halloween: Warrior Soul drop chance", "%", 1m, 100m, 73,
            "Each monster killed anywhere drops a Warrior Soul (+88 honor) this often (its own text; rate GENERATED)."),
        Absolute(CandyExpPerLevelKey, "Halloween: EXP per level for 10 candies", "EXP", 1_000m, 1_000_000m, 74,
            "EXP prize = this x character level (GENERATED). Events EXP rate on top."),
        Absolute(CandyTalentKey, "Halloween: Talent Points for 10 candies", "TP", 10m, 10_000m, 75,
            "Talent Point prize (GENERATED)."),
        Absolute(CandyPetExpPerLevelKey, "Halloween: pet EXP per level for 10 candies", "EXP", 2_000m, 1_000_000m, 76,
            "Pet EXP prize = this x character level, to the summoned pet (GENERATED). No pet out = 'call your pet out'."),
        Absolute(CandyZodiacKey, "Halloween: Zodiac energy for 10 candies", "energy", 20m, 10_000m, 77,
            "Zodiac energy prize, up to the storage limit (GENERATED). A full store pays the EXP prize instead.")
    ];
}
