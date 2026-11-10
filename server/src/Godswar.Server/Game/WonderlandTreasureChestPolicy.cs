using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Domain.Characters;
using Godswar.Server.Domain.World.Content;

namespace Godswar.Server.Game;

internal static class WonderlandTreasureChestPolicy
{
    public const uint FirstObjectId = 5_710;
    public const float InteractionRadius = 8f;
    public const int DialogIndex = 58;
    // The native NPC field is orientation, not a scale multiplier. All eight
    // unique chest templates already share monster_ark_003 in the client.
    public const float CapturedChestFacing = 3.0718751f;

    public static IReadOnlyList<NpcSpawnDefinition> AddChests(IReadOnlyList<NpcSpawnDefinition> existing, byte partyCamp)
    {
        var result = existing.Where(npc => !IsChestKey(npc.NpcKey)).ToList();
        for (var island = 1; island <= 8; island++)
        {
            var id = FirstObjectId + (uint)island - 1;
            if (result.Any(npc => npc.ObjectId == id || npc.InteractionId == id))
                throw new InvalidDataException("Wonderland treasure identity collides with published content.");
            var key = Key(island, partyCamp);
            var position = GetPosition(island, partyCamp);
            var appearance = island == 5 ? 0x0011u | ((uint)partyCamp << 8) : 0x0211u;
            result.Add(new(207, "Fane", key, $"{key}_Male15", id, position.X, position.Z, id, appearance,
                CapturedChestFacing, [], []));
        }
        return result;
    }

    public static bool TryGetIsland(NpcSpawnDefinition npc, out int island)
    {
        island = 0;
        if (npc.ObjectId < FirstObjectId || npc.ObjectId >= FirstObjectId + 8) return false;
        island = (int)(npc.ObjectId - FirstObjectId) + 1;
        if (npc.MapId != 207 ||
            npc.InteractionId != npc.ObjectId || !IsChestKey(npc.NpcKey)) return false;
        var camp = npc.NpcKey == "Fane_012" ? FactionPortalSkillPolicy.AthensCamp :
            FactionPortalSkillPolicy.SpartaCamp;
        var position = GetPosition(island, camp);
        var appearance = island == 5 ? 0x0011u | ((uint)camp << 8) : 0x0211u;
        return npc.X == position.X && npc.Z == position.Z && npc.TemplateKey == $"{npc.NpcKey}_Male15" &&
            npc.AppearanceType == appearance && npc.Facing == CapturedChestFacing &&
            (npc.NpcKey == Key(island, FactionPortalSkillPolicy.SpartaCamp) ||
             npc.NpcKey == Key(island, FactionPortalSkillPolicy.AthensCamp));
    }

    public static WonderlandPosition GetPosition(int island, byte partyCamp)
    {
        if (partyCamp is not (FactionPortalSkillPolicy.SpartaCamp or FactionPortalSkillPolicy.AthensCamp))
            throw new ArgumentOutOfRangeException(nameof(partyCamp));
        // Native faction-specific chest anchors: Fane013 for Sparta, Fane012 for Athens.
        return island == 5 && partyCamp == FactionPortalSkillPolicy.SpartaCamp
            ? new(-137f, 0f, 155f) : WonderlandTerrainPolicy.GetIsland(island).TreasureChest;
    }

    public static bool IsUnlocked(WonderlandSnapshot run, int island, DateTimeOffset now) =>
        island is >= 1 and <= 8 && run.Clears.Any(clear => clear.Island == island && now >= clear.ClearedAt) &&
        (run.State == WonderlandRunState.Active && now < run.Deadline ||
         WonderlandCompletionPolicy.IsTreasureWindowOpen(run, now));

    private static bool IsChestKey(string key) => key.Length == 8 && key.StartsWith("Fane_", StringComparison.Ordinal) &&
        int.TryParse(key.AsSpan(5), out var index) && index is >= 8 and <= 16;

    private static string Key(int island, byte partyCamp) => island switch
    {
        1 => "Fane_008", 2 => "Fane_009", 3 => "Fane_010", 4 => "Fane_011",
        5 => partyCamp == FactionPortalSkillPolicy.SpartaCamp ? "Fane_013" : "Fane_012",
        6 => "Fane_014", 7 => "Fane_015", 8 => "Fane_016",
        _ => throw new ArgumentOutOfRangeException(nameof(island))
    };
}
