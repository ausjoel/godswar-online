namespace Godswar.Server.Domain.World.Content;

/// <summary>
/// PAN CHAIN (W3): the Event Transporter's Treasure Land (button 202 -> map 25 <c>Agate</c>) and Sicily
/// (button 700 -> map 32 <c>Sicily</c>) destinations, and their map rosters. Each destination is GATED OFF
/// unless its own switch is on: <c>GODSWAR_TREASURE_LAND=1</c>, <c>GODSWAR_SICILY=1</c>. With a switch off,
/// the captured / scripted answers stay exactly as before (202 -> 1010, 700 -> 2702).
/// </summary>
/// <remarks>
/// Evidence (Development/pans-labyrinth/README.md):
/// - [client] <c>NpcFunTranmit.lua</c> page 1: 202 = <c>NF_L0_29</c> "*Teleport to Treasure Land", 700 =
///   <c>NF_L0_700</c> "*Teleport to Sicily". Page 2: 1010 (<c>NF_L0_36</c> Treasure Land hours), 2001 (level),
///   2700-2704 (Sicily day/spell lines), 2727 (bag full for Pan's Invitation part 4).
/// - [client] Event Transporter greeting: "Treasure Land 10:00pm - 11:00pm 60-140 Sun", "Sicily 10:00pm - 12:00pm
///   25-140 Fri; You can also enter Sicily at any time with a Mystic Spell except Fridays".
/// - [client] Agate <c>Address.ini</c> (map 25 = 商旅宝库, the merchants' treasure vault): Tuck Nets Vender at
///   (189, 4) camp 0 and (-190, 8) camp 1. Sicily <c>Address.ini</c> (map 32): Returning Helper / Quest Envoy /
///   Sicily Merchant at (-168,-198) / (-176,-196) / (-180,-190) camp 0 and (162,-172) / (167,-181) / (168,-176) camp 1.
/// - [IGG] acid=104 Treasure Land: Lv 60-130, Sundays 22:00-23:00, "40 Wooden Tools by the Event Transporter right
///   after stepping onto the Treasure Land". acid=122 Rebuild Sicily: Fridays 22:00-24:00 free, any other day with
///   a Mystic Scroll, Lv 25+, at most five visits a day; acid=164: part 4 on the fifth visit of the day.
/// - the admin F-70: level caps of 120-140 become 200.
/// GENERATED: every NPC position not named above, the arrival points, the appearance words and the 005/006
/// merchant split.
/// </remarks>
internal static class EventDestinationsProtocol
{
    public const string TreasureLandVariable = "GODSWAR_TREASURE_LAND";
    public const string TreasureLandWindowVariable = "GODSWAR_TREASURE_LAND_WINDOW";
    public const string SicilyVariable = "GODSWAR_SICILY";
    public const string SicilyWindowVariable = "GODSWAR_SICILY_WINDOW";

    public const byte TreasureLandMapId = 25;
    public const string TreasureLandScene = "Agate";
    public const int TreasureLandSubId = 202;
    /// <summary>Captured answer to 202: the Treasure Land hours (<c>NF_L0_36</c>), reused as the "closed" line.</summary>
    public const int TreasureLandClosedResult = 1010;
    public const int TreasureLandMinimumLevel = 60;
    public const int TreasureLandMaximumLevel = 200; // client 140, IGG 130; the admin F-70
    public const uint WoodenToolItemId = 10080;
    public const int WoodenToolsOnArrival = 40;

    public const byte SicilyMapId = 32;
    public const string SicilyScene = "Sicily";
    public const int SicilySubId = 700;
    public const int SicilyNotYetResult = 2700;   // Friday, before 22:00
    public const int SicilyTryAnotherDayResult = 2701; // used here for the five-visits-a-day cap
    public const int SicilyNoSpellResult = 2702;
    public const int SicilyPartFourBagFullResult = 2727;
    public const int SicilyMinimumLevel = 25;
    public const int SicilyMaximumLevel = 200; // client 140; the admin F-70
    public const int SicilyVisitsPerDay = 5;
    /// <summary>"Sicilian Spell" (client item 3819): "be transported to Sicily" by the Event Transporter.</summary>
    public const uint SicilianSpellItemId = 3819;

    public const int LevelResult = CursedLandTransportProtocol.LevelRequirementResultSubId;
    public const int HomeFunction = CursedLandTransportProtocol.HomeTeleportFunction;
    public const float InteractionRadius = 12f;

    public const uint FirstTreasureLandObjectId = 7821;
    public const uint FirstSicilyObjectId = 7841;

    private const uint ServiceAppearance = 0x211u;
    private const uint HomeAppearance = 0x111u;

    public static bool TreasureLandEnabled => Environment.GetEnvironmentVariable(TreasureLandVariable) == "1";

    public static bool SicilyEnabled => Environment.GetEnvironmentVariable(SicilyVariable) == "1";

    /// <summary><c>schedule</c> (default), <c>always</c> or <c>never</c>; for test servers.</summary>
    public static string WindowMode(string variable) =>
        Environment.GetEnvironmentVariable(variable)?.Trim().ToLowerInvariant() switch
        {
            "always" => "always",
            "never" => "never",
            _ => "schedule"
        };

    /// <summary>Sundays 22:00-23:00 realm time.</summary>
    public static bool TreasureLandOpenAt(DateTime realm) => WindowMode(TreasureLandWindowVariable) switch
    {
        "always" => true,
        "never" => false,
        _ => realm.DayOfWeek == DayOfWeek.Sunday && realm.Hour == 22
    };

    /// <summary>Fridays 22:00-24:00 realm time: free entry. Outside it a Sicilian Spell is needed (not on Fridays).</summary>
    public static bool SicilyFreeAt(DateTime realm) => WindowMode(SicilyWindowVariable) switch
    {
        "always" => true,
        "never" => false,
        _ => realm.DayOfWeek == DayOfWeek.Friday && realm.Hour >= 22
    };

    /// <summary>"except Fridays": the spell does not work on a Friday (realm time). "always"/"never" switch the rule off.</summary>
    public static bool SicilySpellForbiddenAt(DateTime realm) =>
        WindowMode(SicilyWindowVariable) == "schedule" && realm.DayOfWeek == DayOfWeek.Friday;

    public static bool IsTreasureLandClick(string npcKey, int dialogIndex, int subId) =>
        TreasureLandEnabled && CursedLandTransportProtocol.IsEventTransporter(npcKey) &&
        dialogIndex == CursedLandTransportProtocol.EventTransporterDialogIndex && subId == TreasureLandSubId;

    public static bool IsSicilyClick(string npcKey, int dialogIndex, int subId) =>
        SicilyEnabled && CursedLandTransportProtocol.IsEventTransporter(npcKey) &&
        dialogIndex == CursedLandTransportProtocol.EventTransporterDialogIndex && subId == SicilySubId;

    // PAN CHAIN slice 2: every map-25 template in the client Monster.ini is a "[Pet]"-flagged (catchable) model, which
    // the authored-spawn filter keeps out. This switch admits them so treasure-land-spawns.sql loads (default off).
    public const string TreasureLandSpawnsVariable = "GODSWAR_TREASURE_LAND_SPAWNS";

    public static bool TreasureLandSpawnsEnabled => Environment.GetEnvironmentVariable(TreasureLandSpawnsVariable) == "1";

    /// <summary>Whether a catchable (pet-flagged) map-25 template may be used by a GM spawn row.</summary>
    public static bool AdmitsTreasureLandAuthoredSpawn(int mapId) =>
        mapId == TreasureLandMapId && TreasureLandSpawnsEnabled;

    /// <summary>GENERATED arrival beside the player's own camp group (camp 0 = Sparta, 1 = Athens, as in Address.ini).</summary>
    public static (float X, float Z) TreasureLandArrival(byte camp) =>
        camp == Godswar.Server.State.GameDefaults.SpartaCamp ? (183f, 4f) : (-184f, 8f);

    public static (float X, float Z) SicilyArrival(byte camp) =>
        camp == Godswar.Server.State.GameDefaults.SpartaCamp ? (-172f, -193f) : (164f, -177f);

    /// <summary>The Treasure Land (map 25) roster. Quartermasters at the client Tuck Nets points; the rest GENERATED.</summary>
    public static IReadOnlyList<NpcSpawnDefinition> TreasureLandNpcs() =>
    [
        // Athens side (Address.ini camp 1, Tuck Nets Vender -190, 8).
        Create(TreasureLandMapId, TreasureLandScene, "Agate_011", "Agate_011_Male4", FirstTreasureLandObjectId, -190f, 8f, ServiceAppearance),
        Create(TreasureLandMapId, TreasureLandScene, "Agate_002", "Agate_002_Aga1", FirstTreasureLandObjectId + 1, -190f, 14f, HomeAppearance),
        Create(TreasureLandMapId, TreasureLandScene, "Agate_007", "Agate_007_Aga3", FirstTreasureLandObjectId + 2, -186f, 14f, ServiceAppearance),
        Create(TreasureLandMapId, TreasureLandScene, "Agate_009", "Agate_009_Losebook1", FirstTreasureLandObjectId + 3, -190f, 2f, ServiceAppearance),
        // Sparta side (camp 0, Tuck Nets Vender 189, 4).
        Create(TreasureLandMapId, TreasureLandScene, "Agate_012", "Agate_012_Male4", FirstTreasureLandObjectId + 4, 189f, 4f, ServiceAppearance),
        Create(TreasureLandMapId, TreasureLandScene, "Agate_004", "Agate_004_Aga2", FirstTreasureLandObjectId + 5, 189f, -2f, HomeAppearance),
        Create(TreasureLandMapId, TreasureLandScene, "Agate_008", "Agate_008_Aga3", FirstTreasureLandObjectId + 6, 185f, -2f, ServiceAppearance),
        Create(TreasureLandMapId, TreasureLandScene, "Agate_010", "Agate_010_Losebook1", FirstTreasureLandObjectId + 7, 189f, 10f, ServiceAppearance),
    ];

    /// <summary>The Sicily (map 32) roster at the client Address.ini points (camp 1 = Athens, camp 0 = Sparta).</summary>
    public static IReadOnlyList<NpcSpawnDefinition> SicilyNpcs() =>
    [
        Create(SicilyMapId, SicilyScene, "Sicily_001", "Sicily_001_Sicily1", FirstSicilyObjectId, 162f, -172f, HomeAppearance),
        Create(SicilyMapId, SicilyScene, "Sicily_003", "Sicily_003_Sicily2", FirstSicilyObjectId + 1, 167f, -181f, ServiceAppearance),
        Create(SicilyMapId, SicilyScene, "Sicily_005", "Sicily_005_Sicily3", FirstSicilyObjectId + 2, 168f, -176f, ServiceAppearance),
        Create(SicilyMapId, SicilyScene, "Sicily_002", "Sicily_002_Sicily1", FirstSicilyObjectId + 3, -168f, -198f, HomeAppearance),
        Create(SicilyMapId, SicilyScene, "Sicily_004", "Sicily_004_Sicily2", FirstSicilyObjectId + 4, -176f, -196f, ServiceAppearance),
        Create(SicilyMapId, SicilyScene, "Sicily_006", "Sicily_006_Sicily3", FirstSicilyObjectId + 5, -180f, -190f, ServiceAppearance),
    ];

    /// <summary>The map transporters that send a player home with function 97 (Cursed Land home shape).</summary>
    public static bool IsHomeTransporter(NpcSpawnDefinition npc) =>
        npc.MapId == TreasureLandMapId && TreasureLandEnabled && npc.NpcKey is "Agate_002" or "Agate_004" ||
        npc.MapId == SicilyMapId && SicilyEnabled && npc.NpcKey is "Sicily_001" or "Sicily_002";

    /// <summary>Adds the actors to a map 25 / 32 roster while that switch is on; otherwise returns it unchanged.</summary>
    public static IReadOnlyList<NpcSpawnDefinition> AddNpcs(int mapId, IReadOnlyList<NpcSpawnDefinition> existing)
    {
        IReadOnlyList<NpcSpawnDefinition>? added = mapId switch
        {
            TreasureLandMapId when TreasureLandEnabled => TreasureLandNpcs(),
            SicilyMapId when SicilyEnabled => SicilyNpcs(),
            _ => null
        };
        if (added is null)
        {
            return existing;
        }

        var keys = added.Select(static npc => npc.NpcKey).ToHashSet(StringComparer.Ordinal);
        var result = existing.Where(npc => !keys.Contains(npc.NpcKey)).ToList();
        foreach (var npc in added)
        {
            if (result.Any(other => other.ObjectId == npc.ObjectId || other.InteractionId == npc.InteractionId))
            {
                throw new InvalidDataException($"Event destination actor {npc.NpcKey} collides with published content.");
            }

            result.Add(npc);
        }

        return result;
    }

    private static NpcSpawnDefinition Create(byte map, string scene, string key, string template, uint objectId,
        float x, float z, uint appearance) =>
        new(map, scene, key, template, objectId, x, z, objectId, appearance, 0f, [], []);
}
