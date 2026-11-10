using Godswar.Server.Domain.World.Content;

namespace Godswar.Server.Application.EventsNpc;

// EVENTSNPC: event NPCs that no map publishes, added to a map's roster at map entry while their event
// switch is on. Positions: the Health Manual / Sheepskin Scroll descriptions (EquipDescription.dat) and
// the wiki-12 relic list; Athens Fitness Trainers 1 and 3 use the reference capture's own placement
// (CapturedNpcPlacements: 85,-116 and 44.5,27.8). Templates are the client's own NPC.INI keys
// (NpcTemplateSeed). Object ids 5700-5733 are used by nothing else (npc_spawn_definitions, gm_npc_spawns,
// the capture, Wonderland 5205-5221, Lelantine 5615-5622; checked 2026-11-03).
internal static class EventsNpcSpawnPolicy
{
    private const uint AthensAppearance = 0x111; // captured Athens_Newbie_024/026
    private const uint SpartaAppearance = 0x11; // captured Sparta_078/079

    private readonly record struct EventNpc(bool Marathon, short MapId, string NpcKey, string Template, uint ObjectId,
        float X, float Z, float Facing);

    private static readonly EventNpc[] All =
    [
        // Fitness Trainers 1-5, suburbs (map 2 Athens_Newbie, map 4 Sparta_Newbie).
        new(true, 2, "Athens_Newbie_024", "Athens_Newbie_024_FemMale3", 5705, 85f, -116f, 1.7f),
        new(true, 2, "Athens_Newbie_025", "Athens_Newbie_025_FemMale3", 5706, 202f, 167f, 1.7f),
        new(true, 2, "Athens_Newbie_026", "Athens_Newbie_026_FemMale3", 5707, 44.5f, 27.8f, 2.3f),
        new(true, 2, "Athens_Newbie_027", "Athens_Newbie_027_FemMale3", 5708, -178f, 228f, 1.7f),
        new(true, 2, "Athens_Newbie_028", "Athens_Newbie_028_FemMale3", 5709, -178f, -192f, 1.7f),
        new(true, 4, "Sparta_Newbie_024", "Sparta_Newbie_024_FemMale3", 5700, 85f, -116f, 1.7f),
        new(true, 4, "Sparta_Newbie_025", "Sparta_Newbie_025_FemMale3", 5701, 202f, 167f, 1.7f),
        new(true, 4, "Sparta_Newbie_026", "Sparta_Newbie_026_FemMale3", 5702, 44.5f, 27.8f, 2.3f),
        new(true, 4, "Sparta_Newbie_027", "Sparta_Newbie_027_FemMale3", 5703, -178f, 228f, 1.7f),
        new(true, 4, "Sparta_Newbie_028", "Sparta_Newbie_028_FemMale3", 5704, -178f, -192f, 1.7f),
        // Fitness Trainers 8-10, capitals: published rows exist but the capture never placed them, so
        // CapturedNpcPlacementPolicy drops them ("not-in-capture"). Same positions as their rows.
        new(true, 0, "Sparta_080", "Sparta_080_FemMale3", 5710, -73f, -184f, 1.7f),
        new(true, 0, "Sparta_081", "Sparta_081_FemMale3", 5711, -78f, -25f, 2.3f),
        new(true, 0, "Sparta_082", "Sparta_082_FemMale3", 5712, -102f, -115f, 1.7f),
        new(true, 1, "Athens_080", "Athens_080_FemMale3", 5713, -73f, -184f, 1.7f),
        new(true, 1, "Athens_081", "Athens_081_FemMale3", 5714, -78f, -25f, 2.3f),
        new(true, 1, "Athens_082", "Athens_082_FemMale3", 5715, -102f, -115f, 1.7f),
        // Ancient Relics 1-7 (wiki-12 and the scroll descriptions).
        new(false, 2, "Athens_Newbie_029", "Athens_Newbie_029_Losebook1", 5720, -174f, -193f, 1.7f),
        new(false, 11, "Marathon_All_011", "Marathon_All_011_Losebook1", 5721, -195f, 210f, 1.7f),
        new(false, 3, "Parnitha_1_023", "Parnitha_1_023_Losebook1", 5722, -200f, 207f, 1.7f),
        new(false, 3, "Parnitha_1_024", "Parnitha_1_024_Losebook1", 5723, 10f, -175f, 1.7f),
        new(false, 11, "Marathon_All_012", "Marathon_All_012_Losebook1", 5724, -215f, -204f, 1.7f),
        new(false, 2, "Athens_Newbie_030", "Athens_Newbie_030_Losebook1", 5725, 202f, 233f, 1.7f),
        new(false, 18, "Megara_All_013", "Megara_All_013_Losebook1", 5726, -200f, -128f, 1.7f),
        new(false, 4, "Sparta_Newbie_029", "Sparta_Newbie_029_Losebook1", 5727, -174f, -193f, 1.7f),
        new(false, 13, "Peloponnese_All_011", "Peloponnese_All_011_Losebook1", 5728, -205f, -196f, 1.7f),
        new(false, 5, "Nemea_1_023", "Nemea_1_023_Losebook1", 5729, -180f, 209f, 1.7f),
        new(false, 5, "Nemea_1_024", "Nemea_1_024_Losebook1", 5730, 161f, 202f, 1.7f),
        new(false, 13, "Peloponnese_All_012", "Peloponnese_All_012_Losebook1", 5731, 110f, 186f, 1.7f),
        new(false, 4, "Sparta_Newbie_030", "Sparta_Newbie_030_Losebook1", 5732, 202f, 233f, 1.7f),
        new(false, 16, "Argolis_All_013", "Argolis_All_013_Losebook1", 5733, 194f, 126f, 1.7f)
    ];

    private static readonly Dictionary<short, string> SceneKeys = new()
    {
        [0] = "Sparta", [1] = "Athens", [2] = "Athens_Newbie", [3] = "Parnitha_1", [4] = "Sparta_Newbie",
        [5] = "Nemea_1", [11] = "Marathon_All", [13] = "Peloponnese_All", [16] = "Argolis_All", [18] = "Megara_All"
    };

    /// <summary>
    /// The event NPCs for one map, with the events that own them switched on. An NPC whose key, object id
    /// or interaction id is already on the map is skipped, so nothing published is ever doubled.
    /// </summary>
    public static IReadOnlyList<NpcSpawnDefinition> For(short mapId, IReadOnlyList<NpcSpawnDefinition> existing,
        bool lostBook, bool marathon)
    {
        if (!lostBook && !marathon || !SceneKeys.TryGetValue(mapId, out var scene))
        {
            return [];
        }

        var keys = existing.Select(static npc => npc.NpcKey).ToHashSet(StringComparer.Ordinal);
        var ids = existing.SelectMany(static npc => new[] { npc.ObjectId, npc.InteractionId }).ToHashSet();
        var added = new List<NpcSpawnDefinition>();
        foreach (var npc in All)
        {
            if (npc.MapId != mapId || (npc.Marathon ? !marathon : !lostBook) ||
                keys.Contains(npc.NpcKey) || ids.Contains(npc.ObjectId))
            {
                continue;
            }

            var appearance = mapId is 1 or 2 or 3 or 11 or 18 ? AthensAppearance : SpartaAppearance;
            added.Add(new NpcSpawnDefinition(mapId, scene, npc.NpcKey, npc.Template, npc.ObjectId, npc.X, npc.Z,
                npc.ObjectId, appearance, npc.Facing, [], []));
        }

        return added;
    }

    public static int Count => All.Length;
}
