using Godswar.Server.Application.Finalise2;
using Godswar.Server.State;

namespace Godswar.Server.Domain.World.Content;

/// <summary>
/// PROF slice 2: Romance Island (runtime map 45, scene <c>Love_Island</c>, client map 232), the gathering map.
/// Live while the hot rate <c>profession.gathering</c> is 1 (default).
/// </summary>
/// <remarks>
/// <para>
/// Client data: the NPC keys, templates and names (NPC.INI, NpcName.dat: Mine Vein, Hunting Range, Nifty Garden,
/// Gathering Node Transporter, Main City Transporter), CollNpc.ini, the Event Transporter button 600
/// (NF_L0_LI600 "*To Romance Island") and its refusals 3601 (level 70) / 3602 (closed).
/// IGG (guide 359, news 1252): "The Event Transporter in the main city will transport any Level 70+ player onto
/// the map"; 9 gathering points, 3 each of Mine Vein, Hunting Range and Nifty Garden (the client lists 5 of each).
/// </para>
/// <para>
/// GENERATED: every position (the client ships an empty Monster/Love_Island/Address.ini and no spawns;
/// tools/professions/place_gather_nodes.py walks Map/Love_Island.hmp), the appearance words, and the opening
/// hours: IGG lists short evening windows, this server keeps the island open whenever the switch is on.
/// </para>
/// </remarks>
internal static class RomanceIslandProtocol
{
    public const byte MapId = 45;

    public const string SceneKey = "Love_Island";

    /// <summary>NF_L0_LI600 "*To Romance Island" on page 1 of NpcFunTranmit.lua.</summary>
    public const int EntrySubId = 600;

    /// <summary>NF_L0_GT3601 "Only Level 70+ players are allowed to enter the gathering area!".</summary>
    public const int LevelResultSubId = 3601;

    /// <summary>NF_L0_GT3602 "Please visit when the event is open and available.".</summary>
    public const int ClosedResultSubId = 3602;

    /// <summary>GENERATED: the reachable clear cell nearest the map centre.</summary>
    public static readonly (float X, float Z) Arrival = (0f, 0f);

    public const uint FirstObjectId = 7901;

    private const uint ServiceAppearance = 0x211u;

    private const uint HomeAppearance = 0x111u;

    public static bool IsEnabled => Finalise2Rates.On(Finalise2RateKeys.Gathering);

    /// <summary>GENERATED positions (tools/professions/place_gather_nodes.py on the 1.4.4 client's Love_Island.hmp).</summary>
    private static readonly (string Key, string Template, float X, float Z)[] Placements =
    [
        ("Love_Island_001", "Love_Island_001_WarField2", 108f, -106f),
        ("Love_Island_002", "Love_Island_002_WarField2", 104f, -100f),
        ("Love_Island_003", "Love_Island_003_WarField2", 97f, -102f),
        ("Love_Island_012", "Love_Island_012_WarField2", 97f, -110f),
        ("Love_Island_013", "Love_Island_013_WarField2", 102f, -110f),
        ("Love_Island_004", "Love_Island_004_WarField2", 104f, -18f),
        ("Love_Island_005", "Love_Island_005_WarField2", 99f, -13f),
        ("Love_Island_006", "Love_Island_006_WarField2", 93f, -14f),
        ("Love_Island_014", "Love_Island_014_WarField2", 93f, -22f),
        ("Love_Island_015", "Love_Island_015_WarField2", 100f, -24f),
        ("Love_Island_007", "Love_Island_007_WarField2", 28f, -104f),
        ("Love_Island_008", "Love_Island_008_WarField2", 24f, -98f),
        ("Love_Island_009", "Love_Island_009_WarField2", 17f, -100f),
        ("Love_Island_016", "Love_Island_016_WarField2", 18f, -106f),
        ("Love_Island_017", "Love_Island_017_WarField2", 24f, -110f),
        ("Love_Island_010", "Love_Island_010_WarField2", 4f, -1f),
        ("Love_Island_011", "Love_Island_011_WarField2", -4f, 0f)
    ];

    public static bool IsIslandNpc(NpcSpawnDefinition npc) =>
        npc.MapId == MapId && npc.NpcKey.StartsWith("Love_Island_0", StringComparison.Ordinal);

    public static bool IsHomeTransporter(NpcSpawnDefinition npc) =>
        IsIslandNpc(npc) && npc.NpcKey == "Love_Island_011";

    public static bool IsEntryClick(string npcKey, int dialogIndex, int subId) =>
        IsEnabled &&
        CursedLandTransportProtocol.IsEventTransporter(npcKey) &&
        dialogIndex == CursedLandTransportProtocol.EventTransporterDialogIndex &&
        subId == EntrySubId;

    /// <summary>The seventeen island actors (15 nodes, two transporters).</summary>
    public static IReadOnlyList<NpcSpawnDefinition> Npcs()
    {
        var npcs = new List<NpcSpawnDefinition>(Placements.Length);
        for (var index = 0; index < Placements.Length; index++)
        {
            var (key, template, x, z) = Placements[index];
            var objectId = FirstObjectId + (uint)index;
            npcs.Add(new(MapId, SceneKey, key, template, objectId, x, z, objectId,
                key == "Love_Island_011" ? HomeAppearance : ServiceAppearance, 0f, [], []));
        }

        return npcs;
    }

    /// <summary>The position of a node's actor, if it is one of the island's.</summary>
    public static bool TryGetPlacement(string npcKey, out float x, out float z)
    {
        foreach (var placement in Placements)
        {
            if (placement.Key == npcKey)
            {
                (x, z) = (placement.X, placement.Z);
                return true;
            }
        }

        (x, z) = (0f, 0f);
        return false;
    }

    /// <summary>
    /// Adds the actors to a map-45 roster while the switch is on. An actor whose id is already taken is left out
    /// (and logged) rather than failing the login.
    /// </summary>
    public static IReadOnlyList<NpcSpawnDefinition> AddNpcs(int mapId, IReadOnlyList<NpcSpawnDefinition> existing)
    {
        if (mapId != MapId || !IsEnabled)
        {
            return existing;
        }

        var result = existing.Where(npc => !IsIslandNpc(npc)).ToList();
        foreach (var npc in Npcs())
        {
            if (result.Any(other => other.ObjectId == npc.ObjectId || other.InteractionId == npc.InteractionId))
            {
                Console.Error.WriteLine($"[gather] actor id {npc.ObjectId} ({npc.NpcKey}) collides with map content; skipped");
                continue;
            }

            result.Add(npc);
        }

        return result;
    }
}
