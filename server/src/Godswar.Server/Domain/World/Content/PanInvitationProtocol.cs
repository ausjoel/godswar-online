using System.Collections.Frozen;

namespace Godswar.Server.Domain.World.Content;

/// <summary>
/// PAN CHAIN (W3): the Pan's Envoys (client function 11, <c>NpcFunMessenger.lua</c>, NPC_FLAG_SYS_MESSENGER) and the
/// Avatar of Pan's certificate draw (function 10, <c>NpcFunPan.lua</c>, NPC_FLAG_SYS_PAN).
/// Switches: <c>GODSWAR_PAN_ENVOY=1</c> (capital envoy: part 6, letter, letter + certificate; Treasure Land envoy:
/// part 1 for a gem) and <c>GODSWAR_PAN_AVATAR_DRAW=1</c> (needs <c>GODSWAR_PAN_LABYRINTH=1</c> to reach the map).
/// </summary>
/// <remarks>
/// Reply numbers are the FIRST branch of the client script that draws them (Lua elseif stops at the first match):
/// page 2 of NpcFunMessenger.lua lists 301/1001 twice and 11/31 mean the Treasure Land envoy's lines, so the capital
/// replies use 11 (part 6), 32 (letter + certificate, MSER31), 51 (once a day, MSER111), 52 (bag full, MSER102),
/// 101 (not enough honour) and 1001 (follow the procedure). The page-3 letter form answers 42/41/401/102/1001.
/// </remarks>
internal static class PanInvitationProtocol
{
    public const string EnvoyVariable = "GODSWAR_PAN_ENVOY";
    public const string AvatarDrawVariable = "GODSWAR_PAN_AVATAR_DRAW";

    public const int EnvoyFunction = 11;
    public const int AvatarFunction = 10;

    public const uint PartOne = 3892, PartTwo = 3893, PartThree = 3894, PartFour = 3895, PartFive = 3896, PartSix = 3897;
    public const uint LetterItemId = 3898;
    public const uint CertificateItemId = 3899;
    public static readonly uint[] Parts = [PartOne, PartTwo, PartThree, PartFour, PartFive, PartSix];

    /// <summary>"You can exchange 400 honor for Pan's Invitation part 6" (NF_L0_MSER1000); IGG: once a day.</summary>
    public const int PartSixHonorCost = 400;

    /// <summary>IGG acid=164: "Each week there are only 2 Pan's Invitation parts" from Treasure Land, Cursed Zone, Hera's Labyrinth and Sicily.</summary>
    public const int MapPartsPerWeek = 2;

    /// <summary>IGG acid=164 / client XD_P001: ten gifts a day.</summary>
    public const int DrawsPerDay = 10;

    // Capital envoy page 1 (Index 1): text 1000, buttons 1 (part 6), 2 (letter), 3 (letter + certificate).
    public static readonly int[] CapitalOpening = [1000, 1, 2, 3];
    // Treasure Land envoy page 1: text 10001, buttons 11/12/13 (Level 1 Sapphire / Emerald / Crystal -> part 1).
    public static readonly int[] TreasureLandOpening = [10001, 11, 12, 13];

    public const int ButtonPartSix = 1, ButtonLetter = 2, ButtonLetterAndCertificate = 3;
    public const int PartSixGiven = 11, NotEnoughHonor = 101, OncePerDay = 51, BagFullPageTwo = 52, FollowProcedure = 1001;
    public const int LetterAndCertificateGiven = 32, NotAllSix = 301;
    public const int LetterForm = 21;
    public const int LetterGiven = 42, NotAllParts = 41, PartsNotDifferent = 401, BagFullPageThree = 102;

    public const int GemSapphire = 11, GemEmerald = 12, GemCrystal = 13;
    public const int PartOneGiven = 31, NotEnoughItems = 301;

    public static readonly FrozenDictionary<int, uint> GemForButton = new Dictionary<int, uint>
    {
        [GemSapphire] = 4210, // Level 1 Sapphire
        [GemEmerald] = 4220,  // Level 1 Emerald
        [GemCrystal] = 4230,  // Level 1 Crystal
    }.ToFrozenDictionary();

    // Avatar page 1 (NpcFunPan.lua): 1 = ten today, 2 = no certificate, 13 = bag full, 10000+n = "remaining: 9-n".
    public const int DrawLimitReached = 1, NoCertificate = 2, DrawBagFull = 13, DrawRemainingBase = 10000;

    // ---- PAN CHAIN slice 2: the Cursed Zone Pan's Envoys (part 2, NF_L0_MSER521) -------------------------------------
    /// <summary>Own switch for the Cursed Zone envoys' part-2 exchange (default off).</summary>
    public const string CursedEnvoyVariable = "GODSWAR_PAN_CURSED_ENVOY";

    // Page 1 (Index 1) SubID 500: text MSER521 plus one item slot (FirstWin_ItemBtn1), no buttons.
    public static readonly int[] CursedZoneOpening = [500];

    // Page 2 replies: 53 = part 2 given (MSER53), 253 = wrong or no equipment (MSER253), 52 = bag full (MSER102),
    // 51 = once a day (MSER111), 103 = "part 2 twice per week" (MSER103).
    public const int PartTwoGiven = 53, WrongEquipment = 253, PartTwoTwiceAWeek = 103;

    /// <summary>"delicate or above" (client ItemColor.xml BaseLevel3 = Delicate; 1 Common, 2 Enhanced).</summary>
    public const short DelicateQuality = 3;

    /// <summary>"or one with at least two options".</summary>
    public const int MinimumOptions = 2;

    public static bool CursedEnvoyEnabled => Environment.GetEnvironmentVariable(CursedEnvoyVariable) == "1";

    /// <summary>The client's Cursed Zone Pan's Envoys (NpcName "Pan's Envoy"; only Execrativelys3_012 is placed today).</summary>
    public static bool IsCursedZoneEnvoy(string npcKey) => npcKey is
        "Execrativelys_015" or "Execrativelys_016" or "Execrativelys2_015" or "Execrativelys2_016" or
        "Execrativelys3_011" or "Execrativelys3_012";

    /// <summary>The MSER521 rule: delicate quality or better, or at least two options.</summary>
    public static bool QualifiesForPartTwo(short quality, int options) =>
        quality >= DelicateQuality || options >= MinimumOptions;

    public static bool EnvoyEnabled => Environment.GetEnvironmentVariable(EnvoyVariable) == "1";

    public static bool AvatarDrawEnabled =>
        Environment.GetEnvironmentVariable(AvatarDrawVariable) == "1" && PansLabyrinthProtocol.IsEnabled;

    public static bool IsCapitalEnvoy(string npcKey) => npcKey is "Athens_090" or "Sparta_090";

    public static bool IsTreasureLandEnvoy(string npcKey) => npcKey is "Agate_009" or "Agate_010";

    public static bool IsPart(uint itemId) => itemId is >= PartOne and <= PartSix;

    /// <summary>Monday of the realm week, the key of the weekly part counter (finalise2_npc_daily.day).</summary>
    public static DateOnly WeekKey(DateOnly realmDay) => realmDay.AddDays(-(((int)realmDay.DayOfWeek + 6) % 7));
}
