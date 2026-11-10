using Godswar.Server.Domain.World.Content;
using Godswar.Server.State;

namespace Godswar.Server.Infrastructure.WorldContent;

/// <summary>
/// Adds the two client-native Arena transport actors required to bridge the
/// map's disconnected upper-lobby and lower-arena collision components.
/// The reviewed V1 spawn set remains immutable.
/// </summary>
internal static class NpcContentBaselineV2
{
    public const int ExpectedEntryCount =
        NpcContentBaselineV1.ExpectedEntryCount + 2;
    public const string ExpectedRevision =
        "7874E077F45FD13D484A0F05A76E37F416D45679CA42ABC523EE531994464DB4";
    public const string Source = "reviewed-published-npc-baseline-v2";

    public static NpcSpawnDefinition[] LoadDefinitions()
    {
        NpcSpawnDefinition[] definitions =
        [
            .. NpcContentBaselineV1.LoadDefinitions(),
            new(
                DuelArenaTransporterProtocol.MapId,
                "Arena",
                DuelArenaTransporterProtocol.GatekeeperNpcKey,
                "Arena_003_Male18",
                DuelArenaTransporterProtocol.LegacyGatekeeperNpcId,
                X: -64f,
                Z: 92f,
                DuelArenaTransporterProtocol.LegacyGatekeeperNpcId,
                NpcAppearanceDefaults.AppearanceType,
                Facing: 1f,
                Detail10077: [],
                Detail10080: []),
            new(
                DuelArenaTransporterProtocol.MapId,
                "Arena",
                DuelArenaTransporterProtocol.DoorkeeperNpcKey,
                "Arena_002_Male18",
                DuelArenaTransporterProtocol.LegacyDoorkeeperNpcId,
                X: -13f,
                Z: 60f,
                DuelArenaTransporterProtocol.LegacyDoorkeeperNpcId,
                NpcAppearanceDefaults.AppearanceType,
                Facing: 1f,
                Detail10077: [],
                Detail10080: [])
        ];
        return definitions
            .OrderBy(static definition => definition.MapId)
            .ThenBy(
                static definition => definition.NpcKey,
                StringComparer.Ordinal)
            .ThenBy(
                static definition => definition.TemplateKey,
                StringComparer.Ordinal)
            .ThenBy(static definition => definition.ObjectId)
            .ToArray();
    }
}
