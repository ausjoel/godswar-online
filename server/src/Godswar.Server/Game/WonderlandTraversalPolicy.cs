using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Domain.World.Content;

namespace Godswar.Server.Game;

internal static class WonderlandTraversalPolicy
{
    public const uint FirstTeleporterObjectId = WonderlandTransportProtocol.FirstTeleporterObjectId;
    public const uint FinalExitObjectId = 5720;
    private const string FinalExitNpcKey = "Lelantine_Farm_005";
    public const float TriggerRadius = 6f;

    public static IReadOnlyList<NpcSpawnDefinition> AddTeleporters(IReadOnlyList<NpcSpawnDefinition> existing)
    {
        var result = existing.Where(npc => !IsTeleporterKey(npc.NpcKey) && npc.NpcKey != "Fane_017" &&
            !(npc.ObjectId == FinalExitObjectId && npc.NpcKey == FinalExitNpcKey)).ToList();
        for (var island = 1; island <= 8; island++)
        {
            AddUnique(result, GetTeleporter(island));
        }
        AddUnique(result, WonderlandTransportProtocol.Blackmarket);
        return result;
    }

    public static NpcSpawnDefinition GetTeleporter(int island)
    {
        if (island == 1) return WonderlandTransportProtocol.CapturedFirstTeleporter;
        if (island == 2) return WonderlandTransportProtocol.CapturedSecondTeleporter;
        if (island is < 3 or > 8) throw new ArgumentOutOfRangeException(nameof(island));
        // Complete external Sep13 run, with matching authored combat-safe exit.
        // Island8 retains the additional per-player Returning Helper service.
        var position = WonderlandTerrainPolicy.GetIsland(island).Exit;
        var facing = island switch
        {
            3 => 2.3218751f, 4 => 3.0718751f, 5 => 4.7687502f,
            6 => 6.1437502f, 7 => 3.0718751f, 8 => 0f,
            _ => throw new ArgumentOutOfRangeException(nameof(island))
        };
        if (island == 8)
            return WonderlandTransportProtocol.Create(FinalExitNpcKey, FinalExitObjectId,
                position.X, position.Z, facing)
                with { TemplateKey = "Lelantine_Farm_005_WarField1" };
        return WonderlandTransportProtocol.Create($"Fane_{island:000}",
            FirstTeleporterObjectId + (uint)island - 1, position.X, position.Z, facing);
    }

    private static void AddUnique(List<NpcSpawnDefinition> result, NpcSpawnDefinition added)
    {
        if (result.Any(npc => npc.ObjectId == added.ObjectId || npc.InteractionId == added.InteractionId))
            throw new InvalidDataException("Wonderland teleporter identity collides with published content.");
        result.Add(added);
    }

    public static bool IsTeleporterKey(string key) => key.Length == 8 &&
        key.StartsWith("Fane_00", StringComparison.Ordinal) && key[7] is >= '1' and <= '7';

    public static bool TryGetIsland(NpcSpawnDefinition npc, out int island)
    {
        island = 0;
        if (npc.MapId != 207 || npc.ObjectId != FinalExitObjectId && !IsTeleporterKey(npc.NpcKey)) return false;
        island = npc.ObjectId == FinalExitObjectId ? 8 : npc.NpcKey[7] - '0';
        var expected = GetTeleporter(island);
        return npc.ObjectId == expected.ObjectId && npc.InteractionId == npc.ObjectId &&
            npc.NpcKey == expected.NpcKey && npc.TemplateKey == expected.TemplateKey &&
            npc.X == expected.X && npc.Z == expected.Z;
    }

    public static bool RequiresExplicitClick(int island)
    {
        if (island is < 1 or > 8) throw new ArgumentOutOfRangeException(nameof(island));
        // The native NPC advertises function57. Only its Teleport action may
        // travel; approaching either the NPC or a nearby chest never does.
        return true;
    }

    public static bool TryDetect(in AcceptedMapMovementSegment movement, out int sourceIsland)
    {
        sourceIsland = 0;
        return false;
    }
}
