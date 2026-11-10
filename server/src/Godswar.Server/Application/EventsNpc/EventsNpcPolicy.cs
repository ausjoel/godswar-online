namespace Godswar.Server.Application.EventsNpc;

// EVENTSNPC: the rules and the client's own reply numbers for the Lost Book, Marathon and Random Quest
// Manager events. Sources: IGG guide igg-acid=161 (Lost Books), 160 (Marathon), 198 (Treasure Map); the
// GodsArena wiki pages 11, 12, 14 and 41 where the IGG page is silent; the reply numbers and reward lines
// come from the stock client's NpcFunOldMan/Remain/LostBook/Health/Quest.lua and LuaText.lua; item ids
// and the manual/scroll routes from EquipDescription.dat (Healthbook1-57). Anything not in those
// sources is marked GENERATED.
internal static class EventsNpcPolicy
{
    // Client function numbers (NpcFun.lua NPC_FLAG_SYS_*).
    public const int HealthFunction = 22;
    public const int OldManFunction = 23;
    public const int RemainFunction = 25;
    public const int QuestFunction = 52;

    // ---- Lost Book (igg-acid=161, wiki-12, wiki-41) ----
    public const int AthensScrollBase = 3860; // Sheepskin Scroll 1-7, Athens relics (Healthbook35-41)
    public const int SpartaScrollBase = 3885; // Sheepskin Scroll 1-7, Sparta relics (Healthbook51-57)
    public const int ChapterBase = 3867; // Lost Book Chapter 1-7
    public const int IncompleteLostBook = 3874;
    public const int LostBook = 3875;
    public const int IncompleteWishesPerDay = 5; // IGG: "Up to 5 Incomplete Lost Books ... each day"
    public const int CompleteWishesPerDay = 2; // IGG: "only 2 Lost Books can be exchanged this way each day"

    // GENERATED: IGG says a relic exchange may give "a Lost Book"; no rate is published. 1 in 100.
    public const int RelicLostBookChancePercent = 1;

    public static int Scroll(byte camp, int number) =>
        (camp == AthensCampId ? AthensScrollBase : SpartaScrollBase) + number - 1;

    public static int Chapter(int number) => ChapterBase + number - 1;

    /// <summary>EXP per relic exchange by level, in 500s (wiki-12; the client line multiplies by 500).</summary>
    public static int RelicExperienceBand(int level) => level switch
    {
        < 60 => 1,
        < 80 => 2,
        < 100 => 3,
        <= 120 => 4,
        _ => 5
    };

    // NpcFunRemain.lua page 2: one exchange (tail, + 1000 * band) and "exchange all" (tail, + 1000 * band
    // + 10000 * count), per chapter 1-7. The two families do not follow the same order in the script.
    private static readonly int[] RelicSingleLine = [203, 214, 215, 216, 217, 218, 219];
    private static readonly int[] RelicAllLine = [220, 224, 222, 223, 225, 221, 226];
    public const int RelicLostBookLine = 204;

    public static int RelicSingleReply(int chapter, int band) => RelicSingleLine[chapter - 1] + 1000 * band;

    public static int RelicAllReply(int chapter, int band, int count) =>
        RelicAllLine[chapter - 1] + 1000 * band + 10000 * count;

    /// <summary>The Lost Book wish header: "You still have N wishes left" is drawn as 10 - (SubID-101)/2000.</summary>
    public static int WishHeader(int remaining) => 101 + 2000 * (10 - Math.Clamp(remaining, 0, 10));

    internal readonly record struct WishReward(int Line, int ExperiencePerLevel, int TalentPoints, int ItemId, int Count)
    {
        /// <summary>The reply number: EXP lines carry the level in 2000s (NpcFunLostBook.lua).</summary>
        public int Reply(int level) => ExperiencePerLevel > 0 ? Line + 2000 * level : Line;
    }

    // wiki-41 tables, identical to the client's NF_L0_BK602-711 / 802-905 lines. The draw is uniform:
    // the weights are GENERATED (no source gives them).
    public static readonly WishReward[] IncompleteWishes =
    [
        new(602, 100, 0, 0, 0), new(603, 300, 0, 0, 0), new(604, 600, 0, 0, 0),
        new(605, 0, 10, 0, 0), new(606, 0, 40, 0, 0),
        new(700, 0, 0, 4230, 2), new(701, 0, 0, 4230, 4), new(702, 0, 0, 4231, 1),
        new(703, 0, 0, 4220, 1), new(704, 0, 0, 4210, 1), new(705, 0, 0, 4200, 1),
        new(706, 0, 0, 4221, 2), new(707, 0, 0, 4211, 2), new(708, 0, 0, 4201, 2),
        new(709, 0, 0, 9960, 1), new(710, 0, 0, 4010, 5), new(711, 0, 0, 4040, 5)
    ];

    public static readonly WishReward[] CompleteWishes =
    [
        new(802, 5000, 0, 0, 0), new(803, 10000, 0, 0, 0),
        new(804, 0, 100, 0, 0), new(805, 0, 200, 0, 0), new(806, 0, 300, 0, 0),
        new(900, 0, 0, 4231, 4), new(901, 0, 0, 4232, 1), new(902, 0, 0, 4222, 1),
        new(903, 0, 0, 4212, 1), new(904, 0, 0, 4202, 1),
        // 905 is the client's generic "Wish granted! Check your Bag"; wiki-41's last row is Quartz Plate 4.
        new(905, 0, 0, 9963, 1)
    ];

    /// <summary>Ancient Relics N by NPC key (NpcName.dat).</summary>
    public static int RelicNumber(string npcKey) => npcKey switch
    {
        "Athens_Newbie_029" or "Sparta_Newbie_029" => 1,
        "Marathon_All_011" or "Peloponnese_All_011" => 2,
        "Parnitha_1_023" or "Nemea_1_023" => 3,
        "Parnitha_1_024" or "Nemea_1_024" => 4,
        "Marathon_All_012" or "Peloponnese_All_012" => 5,
        "Athens_Newbie_030" or "Sparta_Newbie_030" => 6,
        "Megara_All_013" or "Argolis_All_013" => 7,
        _ => 0
    };

    // ---- Marathon (igg-acid=160; client NF_L0_H*) ----
    public const int MinimumMarathonLevel = 60; // IGG and SM_Msg_01
    public const int RacesPerDay = 6; // IGG
    public const int FirstRacesBindingGold = 180; // IGG and NF_L0_H1112x
    public const int FirstRacesCount = 2;
    public const int LaterRaceBindingGold = 90; // IGG and NF_L0_H1112
    public const int AthensManualBase = 3826; // Health Manual 1-17, Healthbook1-17 (Athens suburbs)
    public const int SpartaManualBase = 3843; // Health Manual 1-17, Healthbook18-34 (Sparta suburbs)
    public const int ManualCount = 17;

    public static int Manual(byte camp, int number) =>
        (camp == AthensCampId ? AthensManualBase : SpartaManualBase) + number - 1;

    // Which trainer takes Health Manual N, from the manuals' own descriptions. Athens Healthbook6 reads
    // "Exchange it for Health Manual 4 from Fitness Trainer 4"; 4 is a client typo for 7.
    private static readonly int[] AthensRoute = [3, 4, 5, 3, 2, 4, 3, 1, 6, 7, 9, 10, 9, 8, 7, 6, 1];
    private static readonly int[] SpartaRoute = [2, 3, 4, 5, 3, 5, 2, 1, 6, 7, 10, 8, 7, 9, 7, 6, 1];

    public static int TrainerFor(byte camp, int manual) =>
        (camp == AthensCampId ? AthensRoute : SpartaRoute)[manual - 1];

    /// <summary>Fitness Trainer N by NPC key: 1-5 in the suburbs, 6-10 in the capital.</summary>
    public static int TrainerNumber(string npcKey)
    {
        if (npcKey.Length < 3 || !int.TryParse(npcKey.AsSpan(npcKey.Length - 3), out var tail))
        {
            return 0;
        }

        if (npcKey.StartsWith("Athens_Newbie_", StringComparison.Ordinal) ||
            npcKey.StartsWith("Sparta_Newbie_", StringComparison.Ordinal))
        {
            return tail is >= 24 and <= 28 ? tail - 23 : 0;
        }

        return (npcKey.StartsWith("Athens_", StringComparison.Ordinal) ||
                npcKey.StartsWith("Sparta_", StringComparison.Ordinal)) && tail is >= 78 and <= 82
            ? tail - 72
            : 0;
    }

    /// <summary>IGG: 12:00am-11:55pm, Monday through Saturday (realm time).</summary>
    public static bool IsRaceOpen(DateTimeOffset realmTime) =>
        realmTime.DayOfWeek != DayOfWeek.Sunday &&
        realmTime.TimeOfDay < new TimeSpan(23, 55, 0);

    public static int RaceBindingGold(int finishedToday) =>
        finishedToday < FirstRacesCount ? FirstRacesBindingGold : LaterRaceBindingGold;

    // ---- Random Quest Manager (igg-acid=198, wiki-14) ----
    public const int MinimumRandomQuestLevel = 70; // wiki-14 and NF_L0_QT220
    public const int ChainLength = 10;
    // Two "Quest Sack" items exist with the same text. GENERATED assignment: 3983 Athens, 3984 Sparta,
    // the order every other faction pair in this range uses (manuals, scrolls).
    public const int AthensQuestSack = 3983;
    public const int SpartaQuestSack = 3984;
    public const int ExpPill = 4174;
    public const int TalentStone3 = 4182;
    public const int AthensTreasureMap = 4470;
    public const int SpartaTreasureMap = 4471;
    public const int MaxPillsPerTurnIn = 99; // wiki-14 "up to 99 at a time"

    // Random Quest Scroll items: Athens 4678-4727 open quests 1399-1448, Sparta 4728-4777 open 399-448
    // (ItemBaseAttribute "Quest"; descriptions "Exclusive to Athenians/Spartans").
    public static int ScrollItemFirst(byte camp) => camp == AthensCampId ? 4678 : 4728;

    public static uint ScrollQuestFirst(byte camp) => camp == AthensCampId ? 1399u : 399u;

    public const int ScrollCount = 50;

    public static int QuestSack(byte camp) => camp == AthensCampId ? AthensQuestSack : SpartaQuestSack;

    public static int TreasureMap(byte camp) => camp == AthensCampId ? AthensTreasureMap : SpartaTreasureMap;

    public static bool IsQuestSack(int itemId) => itemId is AthensQuestSack or SpartaQuestSack;

    public static bool IsTreasureMap(int itemId) => itemId is AthensTreasureMap or SpartaTreasureMap;

    public static bool IsChainQuest(uint questId) => questId is >= 399u and <= 448u or >= 1399u and <= 1448u;

    /// <summary>wiki-14: 1 pill after the first quest, up to 10 for the last.</summary>
    public static int PillsForStep(int step) => Math.Clamp(step, 1, ChainLength);

    // GENERATED: the IGG page promises "valuable crystals and precious gems" and a possible Summoning
    // Vessel; no table is published, so this one is made up. One entry is drawn per map.
    public static readonly (int ItemId, int Count, int Weight)[] TreasureLoot =
    [
        (4231, 2, 20), (4232, 1, 10), (4221, 1, 12), (4211, 1, 12), (4201, 1, 12),
        (4222, 1, 4), (4212, 1, 4), (4202, 1, 4), (ExpPill, 5, 18), (4472, 1, 4)
    ];

    public static (int ItemId, int Count) DrawTreasure(int roll)
    {
        var total = TreasureLoot.Sum(static entry => entry.Weight);
        var point = ((roll % total) + total) % total;
        foreach (var (itemId, count, weight) in TreasureLoot)
        {
            if (point < weight)
            {
                return (itemId, count);
            }

            point -= weight;
        }

        return (TreasureLoot[0].ItemId, TreasureLoot[0].Count);
    }

    private const byte AthensCampId = 1; // FactionPortalSkillPolicy.AthensCamp
}
