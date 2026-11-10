using Godswar.Server.Application.World;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Game;
using Godswar.Server.Infrastructure.WorldContent;

namespace Godswar.Server.ProtocolChecks;

internal static partial class DuelArenaTransporterProtocolChecks
{
    public const string CheckName =
        "Duel Arena paired same-scene transporter protocol";

    public static Task RunAsync()
    {
        CheckV2PublishedPairAndGeometry();
        CheckV3PublishedPairAndGeometry();
        CheckV5PublishedRosterAndGeometry();
        CheckBoundedRoutes();
        CheckDialogueCapability();
        CheckCapitalArrivalUsesUpperLobby();
        return Task.CompletedTask;
    }

    private static void CheckV2PublishedPairAndGeometry()
    {
        var arena = NpcContentBaselineV2.LoadDefinitions()
            .Where(static npc =>
                npc.MapId == DuelArenaTransporterProtocol.MapId)
            .OrderBy(static npc => npc.NpcKey, StringComparer.Ordinal)
            .ToArray();
        Check.True(
            arena.Length == 2 &&
            arena[0].NpcKey == "Arena_002" &&
            arena[0].TemplateKey == "Arena_002_Male18" &&
            arena[0].InteractionId ==
                DuelArenaTransporterProtocol.LegacyDoorkeeperNpcId &&
            arena[1].NpcKey == "Arena_003" &&
            arena[1].TemplateKey == "Arena_003_Male18" &&
            arena[1].InteractionId ==
                DuelArenaTransporterProtocol.LegacyGatekeeperNpcId,
            "Arena V2 publishes only the canonical Doorkeeper/Gatekeeper " +
            "pair");
        Check.True(
            arena[0].X == -13f && arena[0].Z == 60f &&
            arena[1].X == -64f && arena[1].Z == 92f &&
            DuelArenaTransporterProtocol.UpperArrivalX == -82f &&
            DuelArenaTransporterProtocol.UpperArrivalZ == 92f &&
            DuelArenaTransporterProtocol.LowerArrivalX == -6f &&
            DuelArenaTransporterProtocol.LowerArrivalZ == 20f,
            "V2 spawn and arrival points remain pinned to their historical " +
            "golden coordinates");
    }

    private static void CheckV3PublishedPairAndGeometry()
    {
        var arena = NpcContentBaselineV3.LoadDefinitions()
            .Where(static npc =>
                npc.MapId == DuelArenaTransporterProtocol.MapId)
            .OrderBy(static npc => npc.NpcKey, StringComparer.Ordinal)
            .ToArray();
        Check.True(
            arena.Length == 2 &&
            arena[0].NpcKey ==
                DuelArenaTransporterProtocol.DoorkeeperNpcKey &&
            arena[0].InteractionId ==
                DuelArenaTransporterProtocol.LegacyDoorkeeperNpcId &&
            arena[0].X ==
                DuelArenaTransporterProtocol.DoorkeeperSpawnX &&
            arena[0].Z ==
                DuelArenaTransporterProtocol.DoorkeeperSpawnZ &&
            arena[0].X == -7f && arena[0].Z == 28f &&
            arena[1].NpcKey ==
                DuelArenaTransporterProtocol.GatekeeperNpcKey &&
            arena[1].InteractionId ==
                DuelArenaTransporterProtocol.LegacyGatekeeperNpcId &&
            arena[1].X ==
                DuelArenaTransporterProtocol.GatekeeperSpawnX &&
            arena[1].Z ==
                DuelArenaTransporterProtocol.GatekeeperSpawnZ &&
            arena[1].X == -92f && arena[1].Z == 92f,
            "Arena V3 publishes the corrected exact Doorkeeper and " +
            "Gatekeeper positions");
        Check.True(
            DuelArenaTransporterProtocol.MaximumInteractionDistance == 12f &&
            WorldSectorVisibilityTracker<NpcSpawnDefinition>.CellSize == 32 &&
            WorldSectorVisibilityTracker<NpcSpawnDefinition>.NeighborRadius == 1,
            "Arena endpoints use the reviewed 12-unit interaction and " +
            "32-unit radius-one AOI bounds");
        CheckReachableFromOwnArrival(
            arena[0],
            DuelArenaTransporterProtocol.LowerArrivalX,
            DuelArenaTransporterProtocol.LowerArrivalZ,
            "lower Doorkeeper");
        CheckReachableFromOwnArrival(
            arena[1],
            DuelArenaTransporterProtocol.UpperArrivalX,
            DuelArenaTransporterProtocol.UpperArrivalZ,
            "upper Gatekeeper");
    }

    private static void CheckBoundedRoutes()
    {
        var arguments = Arguments();
        Check.True(
            DuelArenaTransporterProtocol.TryResolveDestination(
                DuelArenaTransporterProtocol.GatekeeperNpcKey,
                DuelArenaTransporterProtocol.GatekeeperNpcId,
                DuelArenaTransporterProtocol.MapId,
                DuelArenaTransporterProtocol.DialogIndex,
                DuelArenaTransporterProtocol.TravelSubId,
                arguments,
                out var lower) &&
            lower.Section == DuelArenaSection.LowerArena &&
            lower.TargetX == DuelArenaTransporterProtocol.LowerArrivalX &&
            lower.TargetZ == DuelArenaTransporterProtocol.LowerArrivalZ,
            "upper Gatekeeper resolves only to the lower arena");
        Check.True(
            DuelArenaTransporterProtocol.TryResolveDestination(
                DuelArenaTransporterProtocol.DoorkeeperNpcKey,
                DuelArenaTransporterProtocol.DoorkeeperNpcId,
                DuelArenaTransporterProtocol.MapId,
                DuelArenaTransporterProtocol.DialogIndex,
                DuelArenaTransporterProtocol.TravelSubId,
                arguments,
                out var upper) &&
            upper.Section == DuelArenaSection.UpperLobby &&
            upper.TargetX == DuelArenaTransporterProtocol.UpperArrivalX &&
            upper.TargetZ == DuelArenaTransporterProtocol.UpperArrivalZ,
            "lower Doorkeeper resolves only to the upper lobby");

        var polluted = Arguments();
        polluted[4] = 0;
        Check.True(
            !DuelArenaTransporterProtocol.TryResolveDestination(
                "Arena_003",
                DuelArenaTransporterProtocol.DoorkeeperNpcId,
                57, 1, 1001, arguments, out _) &&
            !DuelArenaTransporterProtocol.TryResolveDestination(
                "Arena_003",
                DuelArenaTransporterProtocol.GatekeeperNpcId,
                56, 1, 1001, arguments, out _) &&
            !DuelArenaTransporterProtocol.TryResolveDestination(
                "Arena_003",
                DuelArenaTransporterProtocol.GatekeeperNpcId,
                57, 2, 1001, arguments, out _) &&
            !DuelArenaTransporterProtocol.TryResolveDestination(
                "Arena_003",
                DuelArenaTransporterProtocol.GatekeeperNpcId,
                57, 1, 1000, arguments, out _) &&
            !DuelArenaTransporterProtocol.TryResolveDestination(
                "Arena_003",
                DuelArenaTransporterProtocol.GatekeeperNpcId,
                57, 1, 1001, polluted, out _) &&
            !DuelArenaTransporterProtocol.TryResolveDestination(
                "Arena_003",
                DuelArenaTransporterProtocol.GatekeeperNpcId,
                57, 1, 1001,
                arguments[..^1], out _),
            "wrong key/ID, map, dialog, sub-ID, or argument path fails " +
            "closed");
    }

    private static void CheckDialogueCapability()
    {
        var routes = NpcDialogueBaselineV19.CreateRoutes()
            .Where(static route => route.NpcKey.StartsWith(
                "Arena_",
                StringComparison.Ordinal))
            .OrderBy(static route => route.NpcKey, StringComparer.Ordinal)
            .ToArray();
        var spawns = NpcContentBaselineV6.LoadDefinitions()
            .Where(static npc =>
                npc.MapId == DuelArenaTransporterProtocol.MapId &&
                DuelArenaTransporterProtocol.IsEndpoint(
                    npc.NpcKey,
                    npc.InteractionId))
            .OrderBy(static npc => npc.NpcKey, StringComparer.Ordinal)
            .ToArray();
        Check.True(
            routes.Length == 2 &&
            NpcDialogueBaselineV19.Profiles.Count(static profile =>
                profile.Behavior ==
                    NpcDialogueBehavior.DuelArenaTransporter) == 1 &&
            routes.Zip(spawns).All(static pair =>
                pair.First.Behavior ==
                    NpcDialogueBehavior.DuelArenaTransporter &&
                pair.First.DialogIndex == 1 &&
                pair.First.InitialMenuSubIds.SequenceEqual([1001]) &&
                NpcDialogueBehaviorRegistry.IsAllowed(
                    pair.Second,
                    pair.First)),
            "one finite V17 profile authorizes both exact Arena endpoints");
    }

    private static void CheckCapitalArrivalUsesUpperLobby()
    {
        Check.True(
            BattlefieldTransporterProtocol.TryResolveDestination(
                "Sparta_056",
                BattlefieldTransporterProtocol.SpartaNpcId,
                sourceMapId: 0,
                camp: 0,
                BattlefieldTransporterProtocol.DialogIndex,
                BattlefieldTransporterProtocol.DuelArenaSubId,
                Arguments(),
                out var destination) &&
            destination.TargetMapId ==
                DuelArenaTransporterProtocol.MapId &&
            destination.TargetX ==
                DuelArenaCapturedLayout.UpperArrivalX &&
            destination.TargetZ ==
                DuelArenaCapturedLayout.UpperArrivalZ,
            "capital Duel Arena entry lands in the upper lobby");
    }

    private static int[] Arguments() => Enumerable.Repeat(
        -1,
        DuelArenaTransporterProtocol.FunctionArgumentCount).ToArray();

    private static void CheckReachableFromOwnArrival(
        NpcSpawnDefinition spawn,
        float arrivalX,
        float arrivalZ,
        string description)
    {
        var deltaX = spawn.X - arrivalX;
        var deltaZ = spawn.Z - arrivalZ;
        var maximumDistance =
            DuelArenaTransporterProtocol.MaximumInteractionDistance;
        Check.True(
            (deltaX * deltaX) + (deltaZ * deltaZ) <=
                maximumDistance * maximumDistance,
            $"Arena {description} is within the 12-unit interaction radius " +
            "of its own arrival");
        Check.True(
            WorldSectorVisibilityTracker<NpcSpawnDefinition>.TryGetCell(
                arrivalX,
                arrivalZ,
                out var arrivalCell) &&
            WorldSectorVisibilityTracker<NpcSpawnDefinition>.TryGetCell(
                spawn.X,
                spawn.Z,
                out var spawnCell) &&
            WorldSectorVisibilityTracker<NpcSpawnDefinition>.IsNeighbor(
                arrivalCell,
                spawnCell),
            $"Arena {description} is visible from its own arrival in the " +
            "32-unit radius-one AOI");
    }
}
