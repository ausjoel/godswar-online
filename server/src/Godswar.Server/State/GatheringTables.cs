namespace Godswar.Server.State;

/// <summary>Which gathering a node offers: the CollNpc.ini Action (also the rabbit model in Collmale/Collfemale.ini).</summary>
internal enum GatherKind : byte
{
    Mine = 0,    // Collmale [0] 挖矿兔子 "mining rabbit"; NpcName "Mine Vein"
    Garden = 1,  // Collmale [1] 采药兔子 "herb rabbit"; NpcName "Nifty Garden"
    Hunting = 2  // Collmale [2] 狩猎兔子 "hunting rabbit"; NpcName "Hunting Range"
}

/// <summary>One CollNpc.ini row: the entity name the client matches (0x6580c0), the node id it sends, the kind.</summary>
internal readonly record struct GatherNode(int Id, string TemplateKey, GatherKind Kind)
{
    /// <summary>The NPC key (template key without its model suffix), e.g. Love_Island_001.</summary>
    public string NpcKey => TemplateKey[..TemplateKey.LastIndexOf('_')];
}

/// <summary>What one gather attempt did.</summary>
internal readonly record struct GatherRoll(bool Success, uint ItemId, int Count);

/// <summary>
/// PROF slice 2 (2026-11-09): gathering on top of the client data. Pure functions, so the ProtocolChecks can run
/// them without a database. Everything the client data does not settle is marked GENERATED.
/// Protocol and evidence: docs/professions/README.md section 6.
/// </summary>
internal static class GatheringTables
{
    /// <summary>NPC_FLAG_SYS_GATHER = 65 (NpcFun/NpcFun.lua, "采矿点"): selects NpcFunGather.lua.</summary>
    public const int GatherFunction = 65;

    /// <summary>NpcFunGather.lua page 1: 100 = "*Resource Gathering" button, 105 = "place the tool" text + item slot.</summary>
    public const int GatherButton = 100;
    public const int PlaceToolText = 105;

    /// <summary>NpcFunGather.lua page 2: 900 tool level, 901-904 wrong tool (mine/hunting/garden/Pan), 905 opens the
    /// gather window (GameAPI:Collction), 906 bag full.</summary>
    public const int ToolLevelTooLow = 900;
    public const int OpenGatherWindow = 905;
    public const int BagFull = 906;

    /// <summary>GENERATED: Activity Points spent on every attempt (the client has no gathering AP field).</summary>
    public const int ApPerAttempt = 20;

    /// <summary>
    /// The client repeats its gather request (10344 state 1) every 2000 ms while the window is gathering
    /// (0x6589f0 compares the tick with 0x7d0). GENERATED: a request less than 1.5 s after the last answered
    /// one gets the silent result 2, so a client can never gather faster than about one attempt per 2 s.
    /// </summary>
    public static readonly TimeSpan MinimumInterval = TimeSpan.FromMilliseconds(1500);

    /// <summary>GENERATED: how far (units) a character may stand from its node and still gather.</summary>
    public const float GatherRadius = 12f;

    /// <summary>Romance Island level gate: NF_L0_GT3601 "Only Level 70+ players are allowed to enter the gathering area!".</summary>
    public const int MinimumLevel = 70;

    /// <summary>
    /// CollNpc.ini verbatim (16 rows). The Name is the NPC.INI section, i.e. the spawn template key: the client
    /// finds the node id by comparing the clicked entity's name with it.
    /// </summary>
    public static readonly GatherNode[] Nodes =
    [
        new(1, "Love_Island_001_WarField2", GatherKind.Mine),
        new(2, "Love_Island_004_WarField2", GatherKind.Hunting),
        new(3, "Love_Island_007_WarField2", GatherKind.Garden),
        new(4, "Pans_Labyrinth_012_Sicily1", GatherKind.Mine),
        new(5, "Love_Island_002_WarField2", GatherKind.Mine),
        new(6, "Love_Island_005_WarField2", GatherKind.Hunting),
        new(7, "Love_Island_008_WarField2", GatherKind.Garden),
        new(8, "Love_Island_003_WarField2", GatherKind.Mine),
        new(9, "Love_Island_006_WarField2", GatherKind.Hunting),
        new(10, "Love_Island_009_WarField2", GatherKind.Garden),
        new(11, "Love_Island_012_WarField2", GatherKind.Mine),
        new(12, "Love_Island_014_WarField2", GatherKind.Hunting),
        new(13, "Love_Island_016_WarField2", GatherKind.Garden),
        new(14, "Love_Island_013_WarField2", GatherKind.Mine),
        new(15, "Love_Island_015_WarField2", GatherKind.Hunting),
        new(16, "Love_Island_017_WarField2", GatherKind.Garden)
    ];

    public static bool TryGetNode(int id, out GatherNode node)
    {
        foreach (var candidate in Nodes)
        {
            if (candidate.Id == id)
            {
                node = candidate;
                return true;
            }
        }

        node = default;
        return false;
    }

    public static bool TryGetNodeByNpcKey(string npcKey, out GatherNode node)
    {
        foreach (var candidate in Nodes)
        {
            if (candidate.NpcKey == npcKey)
            {
                node = candidate;
                return true;
            }
        }

        node = default;
        return false;
    }

    /// <summary>
    /// The tool family a kind needs (ItemBaseAttribute ItemType 52, EquipName.dat): Hoe 12110-12114 for the
    /// Mine Vein (NF_L0_GT101 / NF_L0_LI907), Shovel 12120-12124 for the Nifty Garden (NF_L0_LI112 / LI909 /
    /// GT903; GT103 says Hoe, the other three say Shovel), Arrow 12130-12134 for the Hunting Range (GT102 / LI908).
    /// </summary>
    public static uint ToolFamily(GatherKind kind) => kind switch
    {
        GatherKind.Mine => 12110,
        GatherKind.Garden => 12120,
        _ => 12130
    };

    /// <summary>Tool tier 0..4 (Beginner, Normal, Refined, Demonic, Divine), or -1 when the item is not a tool of that kind.</summary>
    public static int ToolTier(GatherKind kind, uint itemId)
    {
        var tier = (long)itemId - ToolFamily(kind);
        return tier is >= 0 and <= 4 ? (int)tier : -1;
    }

    /// <summary>Whether the item is any gathering tool (12110-12114, 12120-12124, 12130-12134).</summary>
    public static bool IsAnyTool(uint itemId) =>
        itemId is (>= 12110 and <= 12114) or (>= 12120 and <= 12124) or (>= 12130 and <= 12134);

    /// <summary>The tools' PlayLv lower bounds (ItemBaseAttribute): 1, 60, 90, 110, 120.</summary>
    public static int ToolLevel(int tier) => tier switch
    {
        0 => 1,
        1 => 60,
        2 => 90,
        3 => 110,
        _ => 120
    };

    /// <summary>The wrong-tool line of NpcFunGather.lua page 2 for a node: 901 mine, 902 hunting, 903 garden, 904 Pan's vault.</summary>
    public static int WrongToolLine(GatherNode node) =>
        node.Id == 4 ? 904 : node.Kind switch
        {
            GatherKind.Mine => 901,
            GatherKind.Hunting => 902,
            _ => 903
        };

    /// <summary>The description line of NpcFunGather.lua page 1 for a node: 101 mine, 102 hunting, 103 garden, 104 Pan's vault.</summary>
    public static int DescriptionLine(GatherNode node) =>
        node.Id == 4 ? 104 : node.Kind switch
        {
            GatherKind.Mine => 101,
            GatherKind.Hunting => 102,
            _ => 103
        };

    /// <summary>
    /// GENERATED drop sets. The base materials are the ten that blueprints consume and no blueprint produces
    /// (Lifing_Blueprint.xml), so they can only come from gathering or the Ingredients Vendor; they are split by
    /// family name and the IGG guide ("copper ore, silver ore and more" in the mine, "hides and fur" in the
    /// hunting range, "a variety of Herbs" in the garden). Common = the primary material of the tier-1 recipes;
    /// rare = the second-family primaries (12050-12090, used by fewer, higher recipes).
    /// </summary>
    public static (uint[] Common, uint[] Rare) Drops(GatherKind kind) => kind switch
    {
        // Copper Ore, Raw Gem, Divine Fire | Silver Ore, Agate, Pneuma
        GatherKind.Mine => ([12010, 12040, 12020], [12060, 12090, 12070]),
        // Herb | Wormwood
        GatherKind.Garden => ([12030], [12080]),
        // Hard Leather | Fine Fur
        _ => ([12000], [12050])
    };

    /// <summary>GENERATED: success chance by tool tier (EquipDescription: "the better the gathering tool is, the more successful").</summary>
    public static int SuccessPercent(int tier) => 70 + 5 * Math.Clamp(tier, 0, 4);

    /// <summary>GENERATED: chance of a rare material by tool tier: 0, 15, 30, 45, 60 percent.</summary>
    public static int RarePercent(int tier) => 15 * Math.Clamp(tier, 0, 4);

    /// <summary>
    /// One attempt. <paramref name="rolls"/> supplies three numbers in 0..99 (success, rare, pick) and one in
    /// 0..tier (extra count). GENERATED: 1 + extra items, never above 99.
    /// </summary>
    public static GatherRoll Roll(GatherKind kind, int tier, int successRoll, int rareRoll, int pickRoll, int extraRoll)
    {
        tier = Math.Clamp(tier, 0, 4);
        if (successRoll >= SuccessPercent(tier))
        {
            return new(false, 0, 0);
        }

        var (common, rare) = Drops(kind);
        var pool = rareRoll < RarePercent(tier) ? rare : common;
        var item = pool[Math.Clamp(pickRoll, 0, 99) * pool.Length / 100];
        var count = Math.Clamp(1 + Math.Clamp(extraRoll, 0, tier), 1, 99);
        return new(true, item, count);
    }

    /// <summary>Distance check on the server map plane.</summary>
    public static bool InReach(float x, float z, float nodeX, float nodeZ, float radius = GatherRadius) =>
        (x - nodeX) * (x - nodeX) + (z - nodeZ) * (z - nodeZ) <= radius * radius;
}
