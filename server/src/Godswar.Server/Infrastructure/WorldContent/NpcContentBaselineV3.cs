using Godswar.Server.Domain.World.Content;

namespace Godswar.Server.Infrastructure.WorldContent;

/// <summary>
/// Corrects the two Arena transporter placements so each actor is reachable
/// from the collision component it serves. The reviewed V2 spawn set remains
/// immutable.
/// </summary>
internal static class NpcContentBaselineV3
{
    public const int ExpectedEntryCount =
        NpcContentBaselineV2.ExpectedEntryCount;
    public const string ExpectedRevision =
        "916891C9B26B5A51FE10B0F0D619D0C977980C5695C3651C1004EF7FCB6FEEBC";
    public const string Source = "reviewed-published-npc-baseline-v3";

    public static NpcSpawnDefinition[] LoadDefinitions()
    {
        var replacementCount = 0;
        var definitions = NpcContentBaselineV2.LoadDefinitions()
            .Select(definition =>
            {
                if (definition.MapId != DuelArenaTransporterProtocol.MapId)
                {
                    return definition;
                }

                if (definition.NpcKey ==
                        DuelArenaTransporterProtocol.GatekeeperNpcKey &&
                    definition.ObjectId ==
                        DuelArenaTransporterProtocol.LegacyGatekeeperNpcId &&
                    definition.InteractionId ==
                        DuelArenaTransporterProtocol.LegacyGatekeeperNpcId)
                {
                    replacementCount++;
                    return definition with
                    {
                        X = DuelArenaTransporterProtocol.GatekeeperSpawnX,
                        Z = DuelArenaTransporterProtocol.GatekeeperSpawnZ
                    };
                }

                if (definition.NpcKey ==
                        DuelArenaTransporterProtocol.DoorkeeperNpcKey &&
                    definition.ObjectId ==
                        DuelArenaTransporterProtocol.LegacyDoorkeeperNpcId &&
                    definition.InteractionId ==
                        DuelArenaTransporterProtocol.LegacyDoorkeeperNpcId)
                {
                    replacementCount++;
                    return definition with
                    {
                        X = DuelArenaTransporterProtocol.DoorkeeperSpawnX,
                        Z = DuelArenaTransporterProtocol.DoorkeeperSpawnZ
                    };
                }

                return definition;
            })
            .ToArray();
        if (replacementCount != 2 ||
            definitions.Length != ExpectedEntryCount)
        {
            throw new InvalidDataException(
                "The V3 Arena placement overlay requires both transporter " +
                "spawns exactly once.");
        }

        return definitions;
    }
}
