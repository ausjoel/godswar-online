using Godswar.Server.Domain.World.Content;

namespace Godswar.Server.Infrastructure.WorldContent;

/// <summary>
/// Re-keys Arena's five actors into the native NPC object-ID range and moves
/// its Vendor into the AOI shared by the real persisted upper checkpoint and
/// the authored upper arrival. The reviewed V5 spawn set remains immutable.
/// </summary>
internal static class NpcContentBaselineV6
{
    public const int ExpectedEntryCount =
        NpcContentBaselineV5.ExpectedEntryCount;
    public const string ExpectedRevision =
        "F1F1216F6C16EDF845BA7116124538DFDE0CE6EFCC5EB917693950CF15A4604E";
    public const string Source = "reviewed-published-npc-baseline-v6";

    public static NpcSpawnDefinition[] LoadDefinitions()
    {
        var changedCount = 0;
        var definitions = NpcContentBaselineV5.LoadDefinitions()
            .Select(definition => ApplyArenaRelease(
                definition,
                ref changedCount))
            .ToArray();
        if (definitions.Length != ExpectedEntryCount || changedCount != 5)
        {
            throw new InvalidDataException(
                "The V6 Arena overlay must re-key exactly five actors.");
        }

        if (definitions.Select(static definition =>
                (definition.MapId, definition.ObjectId))
            .Distinct().Count() != definitions.Length ||
            definitions.Select(static definition =>
                (definition.MapId, definition.InteractionId))
            .Distinct().Count() != definitions.Length)
        {
            throw new InvalidDataException(
                "The V6 Arena identities collide within a map.");
        }

        return definitions;
    }

    private static NpcSpawnDefinition ApplyArenaRelease(
        NpcSpawnDefinition definition,
        ref int changedCount)
    {
        if (definition.MapId != DuelArenaTransporterProtocol.MapId)
        {
            return definition;
        }

        var identities = definition.NpcKey switch
        {
            DuelArenaNpcRoster.VendorNpcKey => new ArenaIdentity(
                DuelArenaNpcRoster.LegacyVendorNpcId,
                DuelArenaNpcRoster.VendorNpcId),
            DuelArenaTransporterProtocol.DoorkeeperNpcKey => new ArenaIdentity(
                DuelArenaTransporterProtocol.LegacyDoorkeeperNpcId,
                DuelArenaTransporterProtocol.DoorkeeperNpcId),
            DuelArenaTransporterProtocol.GatekeeperNpcKey => new ArenaIdentity(
                DuelArenaTransporterProtocol.LegacyGatekeeperNpcId,
                DuelArenaTransporterProtocol.GatekeeperNpcId),
            DuelArenaNpcRoster.WardNpcKey => new ArenaIdentity(
                DuelArenaNpcRoster.LegacyWardNpcId,
                DuelArenaNpcRoster.WardNpcId),
            DuelArenaNpcRoster.PhysicianNpcKey => new ArenaIdentity(
                DuelArenaNpcRoster.LegacyPhysicianNpcId,
                DuelArenaNpcRoster.PhysicianNpcId),
            _ => null
        };
        if (identities is null)
        {
            return definition;
        }
        if (identities.NativeNpcId is < 1_500 or >= 8_000)
        {
            throw new InvalidDataException(
                $"Arena actor {definition.NpcKey} is outside the native " +
                "NPC object-ID range.");
        }
        if (definition.ObjectId != identities.LegacyNpcId ||
            definition.InteractionId != identities.LegacyNpcId)
        {
            throw new InvalidDataException(
                $"Arena actor {definition.NpcKey} has an unexpected " +
                "legacy identity.");
        }

        changedCount++;
        var released = definition with
        {
            ObjectId = identities.NativeNpcId,
            InteractionId = identities.NativeNpcId
        };
        return definition.NpcKey == DuelArenaNpcRoster.VendorNpcKey
            ? released with
            {
                X = DuelArenaLobbyLayoutV6.VendorSpawnX,
                Z = DuelArenaLobbyLayoutV6.VendorSpawnZ
            }
            : released;
    }

    private sealed record ArenaIdentity(
        uint LegacyNpcId,
        uint NativeNpcId);
}
