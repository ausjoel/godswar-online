using Godswar.Server.Domain.World.Content;

namespace Godswar.Server.Infrastructure.WorldContent;

/// <summary>
/// Clusters Arena's three support actors around the upper arrival and
/// Gatekeeper so the complete lobby roster is apparent in one camera view.
/// The reviewed V4 spawn set remains immutable.
/// </summary>
internal static class NpcContentBaselineV5
{
    public const int ExpectedEntryCount =
        NpcContentBaselineV4.ExpectedEntryCount;
    public const string ExpectedRevision =
        "60E6EAA770B4438FCBFA30490DA7697C01123DC092DF17546AB65B6FCE1408B3";
    public const string Source = "reviewed-published-npc-baseline-v5";

    public static NpcSpawnDefinition[] LoadDefinitions()
    {
        var changedCount = 0;
        var definitions = NpcContentBaselineV4.LoadDefinitions()
            .Select(definition => ApplyArenaLobbyPlacement(
                definition,
                ref changedCount))
            .ToArray();
        if (definitions.Length != ExpectedEntryCount || changedCount != 3)
        {
            throw new InvalidDataException(
                "The V5 Arena lobby overlay must move exactly three " +
                "support actors.");
        }

        return definitions;
    }

    private static NpcSpawnDefinition ApplyArenaLobbyPlacement(
        NpcSpawnDefinition definition,
        ref int changedCount)
    {
        if (definition.MapId != DuelArenaTransporterProtocol.MapId)
        {
            return definition;
        }

        var placement = definition.NpcKey switch
        {
            DuelArenaNpcRoster.VendorNpcKey => new ArenaPlacement(
                DuelArenaNpcRoster.LegacyVendorNpcId,
                DuelArenaLobbyLayoutV5.VendorSpawnX,
                DuelArenaLobbyLayoutV5.VendorSpawnZ,
                DuelArenaLobbyLayoutV5.VendorFacing),
            DuelArenaNpcRoster.WardNpcKey => new ArenaPlacement(
                DuelArenaNpcRoster.LegacyWardNpcId,
                DuelArenaLobbyLayoutV5.WardSpawnX,
                DuelArenaLobbyLayoutV5.WardSpawnZ,
                DuelArenaLobbyLayoutV5.WardFacing),
            DuelArenaNpcRoster.PhysicianNpcKey => new ArenaPlacement(
                DuelArenaNpcRoster.LegacyPhysicianNpcId,
                DuelArenaLobbyLayoutV5.PhysicianSpawnX,
                DuelArenaLobbyLayoutV5.PhysicianSpawnZ,
                DuelArenaLobbyLayoutV5.PhysicianFacing),
            _ => null
        };
        if (placement is null)
        {
            return definition;
        }
        if (definition.ObjectId != placement.NpcId ||
            definition.InteractionId != placement.NpcId)
        {
            throw new InvalidDataException(
                $"Arena support actor {definition.NpcKey} has an " +
                "unexpected identity.");
        }

        changedCount++;
        return definition with
        {
            X = placement.X,
            Z = placement.Z,
            Facing = placement.Facing
        };
    }

    private sealed record ArenaPlacement(
        uint NpcId,
        float X,
        float Z,
        float Facing);
}
