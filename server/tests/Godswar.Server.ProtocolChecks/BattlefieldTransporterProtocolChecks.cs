using Godswar.Server.Domain.World.Content;
using Godswar.Server.Game;
using Godswar.Server.Infrastructure.WorldContent;

namespace Godswar.Server.ProtocolChecks;

internal static class BattlefieldTransporterProtocolChecks
{
    public const string CheckName =
        "Battlefield Transporter stock menu and route protocol";

    public static Task RunAsync()
    {
        CheckWireAndResultContract();
        CheckPublishedEndpointsAndMenus();
        CheckNiMiniNavigation();
        CheckFactionRoutesAndLevelGates();
        CheckStrictActionPaths();
        return Task.CompletedTask;
    }

    private static void CheckWireAndResultContract()
    {
        Check.True(
            BattlefieldTransporterProtocol.DialogIndex == 1 &&
            BattlefieldTransporterProtocol.ActionPacketBytes == 92 &&
            BattlefieldTransporterProtocol.FunctionArgumentCount == 18 &&
            BattlefieldTransporterProtocol.InitialRequestSubId == -1,
            "Battlefield Transporter uses the stock NpcFunTranmit frame");
        Check.True(
            BattlefieldTransporterProtocol.PindusResultDialogIndex == 2 &&
            BattlefieldTransporterProtocol.PindusClosedResultSubId == 8000 &&
            BattlefieldTransporterProtocol
                .PindusMinimumLevelResultDialogIndex == 1 &&
            BattlefieldTransporterProtocol
                .PindusMinimumLevelResultSubId == 8002 &&
            BattlefieldTransporterProtocol.PindusLevelResultSubId == 8001 &&
            BattlefieldTransporterProtocol.NiMiniResultDialogIndex == 3 &&
            BattlefieldTransporterProtocol
                .NiMiniUnavailableResultSubId == 7000,
            "captured event rejection dialogues remain distinct");
        Check.True(
            BattlefieldTransporterProtocol.MaximumInteractionDistance == 12f &&
            BattlefieldTransporterProtocol.MenuContextLifetime ==
                TimeSpan.FromMinutes(2),
            "Battlefield actions use bounded proximity and menu leases");
    }

    private static void CheckPublishedEndpointsAndMenus()
    {
        var published = NpcContentBaselineV1.LoadDefinitions()
            .Where(static npc => npc.NpcKey is
                "Sparta_056" or "Athens_056")
            .OrderBy(static npc => npc.NpcKey, StringComparer.Ordinal)
            .ToArray();
        Check.True(
            BattlefieldTransporterProtocol.SpartaNpcId == 5053 &&
            BattlefieldTransporterProtocol.SpartaNpcId == 5053 &&
            BattlefieldTransporterProtocol.PublishedAthensNpcId == 5195 &&
            published.Length == 2 &&
            published[0].NpcKey == "Athens_056" &&
            published[0].InteractionId ==
                BattlefieldTransporterProtocol.PublishedAthensNpcId &&
            published[1].NpcKey == "Sparta_056" &&
            published[1].InteractionId == 5053,
            "current published Battlefield Transporter IDs are pinned");
        Check.True(
            BattlefieldTransporterProtocol.IsEndpoint("Sparta_056", 5053) &&
            BattlefieldTransporterProtocol.IsEndpoint("Athens_056", 5195) &&
            !BattlefieldTransporterProtocol.IsEndpoint("Sparta_056", 5055) &&
            !BattlefieldTransporterProtocol.IsEndpoint("Sparta_072", 5053) &&
            !BattlefieldTransporterProtocol.IsEndpoint("Athens_056", 5053),
            "only live key and interaction-ID pairs are accepted");
        Check.True(
            BattlefieldTransporterProtocol.TryGetInitialMenu(
                "Sparta_056",
                5053,
                out var spartaMenu) &&
            spartaMenu.SequenceEqual(
                [251, 274, 1001, BattlefieldTransporterProtocol.LelantineFarmSubId]) &&
            BattlefieldTransporterProtocol.TryGetInitialMenu(
                "Athens_056",
                5195,
                out var athensMenu) &&
            athensMenu.SequenceEqual(
                [252, 274, 1001, BattlefieldTransporterProtocol.LelantineFarmSubId]),
            "each capital publishes its exact stock root menu plus the farm entry");
        Check.True(
            !BattlefieldTransporterProtocol.TryGetInitialMenu(
                "Sparta_056",
                5055,
                out var staleMenu) &&
            staleMenu.Count == 0,
            "stale placement IDs cannot acquire a menu");
    }

    private static void CheckNiMiniNavigation()
    {
        Check.True(
            BattlefieldTransporterProtocol.TryGetNiMiniPage(
                1,
                274,
                Arguments(),
                out var page) &&
            page.SequenceEqual([273]),
            "Ni Mini root action opens the current level 70-89 choice");

        var polluted = Arguments();
        polluted[0] = 0;
        Check.True(
            !BattlefieldTransporterProtocol.TryGetNiMiniPage(
                2,
                274,
                Arguments(),
                out _) &&
            !BattlefieldTransporterProtocol.TryGetNiMiniPage(
                1,
                273,
                Arguments(),
                out _) &&
            !BattlefieldTransporterProtocol.TryGetNiMiniPage(
                1,
                274,
                polluted,
                out _) &&
            !BattlefieldTransporterProtocol.TryGetNiMiniPage(
                1,
                274,
                Arguments()[..^1],
                out _),
            "Ni Mini page navigation rejects loose or malformed paths");
    }

    private static void CheckFactionRoutesAndLevelGates()
    {
        var spartaPindus = Resolve(
            "Sparta_056", 5053, sourceMapId: 0, camp: 0, subId: 251);
        var athensPindus = Resolve(
            "Athens_056", 5195, sourceMapId: 1, camp: 1, subId: 252);
        CheckDestination(
            spartaPindus,
            BattlefieldDestinationKind.Pindus,
            sourceMapId: 0,
            targetMapId: 38,
            minimumLevel: 31,
            maximumLevel: 200); // FINALISE2 (the admin F-70): the rate-off Pindus cap 120 -> 200
        CheckDestination(
            athensPindus,
            BattlefieldDestinationKind.Pindus,
            sourceMapId: 1,
            targetMapId: 38,
            minimumLevel: 31,
            maximumLevel: 200); // FINALISE2 (the admin F-70): the rate-off Pindus cap 120 -> 200
        Check.True(
            spartaPindus.TargetX == 14f &&
            spartaPindus.TargetZ == 180f &&
            athensPindus.TargetX == 10f &&
            athensPindus.TargetZ == -183f,
            "Pindus uses the quest-backed faction arrivals");

        var spartaNiMini = Resolve(
            "Sparta_056",
            5053,
            sourceMapId: 0,
            camp: 0,
            subId: 274,
            path: 273);
        var athensNiMini = Resolve(
            "Athens_056",
            5195,
            sourceMapId: 1,
            camp: 1,
            subId: 274,
            path: 273);
        CheckDestination(
            spartaNiMini,
            BattlefieldDestinationKind.NiMiniUpper,
            sourceMapId: 0,
            targetMapId: 34,
            minimumLevel: 50,
            maximumLevel: 89); // FINALISE2 (the admin F-67): Ni Mini 50-89
        CheckDestination(
            athensNiMini,
            BattlefieldDestinationKind.NiMiniUpper,
            sourceMapId: 1,
            targetMapId: 34,
            minimumLevel: 50,
            maximumLevel: 89); // FINALISE2 (the admin F-67): Ni Mini 50-89

        var spartaDuel = Resolve(
            "Sparta_056", 5053, sourceMapId: 0, camp: 0, subId: 1001);
        var athensDuel = Resolve(
            "Athens_056", 5195, sourceMapId: 1, camp: 1, subId: 1001);
        CheckDestination(
            spartaDuel,
            BattlefieldDestinationKind.DuelArena,
            sourceMapId: 0,
            targetMapId: 57,
            minimumLevel: 1,
            maximumLevel: int.MaxValue);
        CheckDestination(
            athensDuel,
            BattlefieldDestinationKind.DuelArena,
            sourceMapId: 1,
            targetMapId: 57,
            minimumLevel: 1,
            maximumLevel: int.MaxValue);
        Check.True(
            spartaDuel.TargetX ==
                DuelArenaCapturedLayout.UpperArrivalX &&
            spartaDuel.TargetZ ==
                DuelArenaCapturedLayout.UpperArrivalZ &&
            athensDuel.TargetX ==
                DuelArenaCapturedLayout.UpperArrivalX &&
            athensDuel.TargetZ ==
                DuelArenaCapturedLayout.UpperArrivalZ,
            "both capital routes enter the Duel Arena upper lobby");
    }

    private static void CheckStrictActionPaths()
    {
        var empty = Arguments();
        Check.True(
            !BattlefieldTransporterProtocol.TryResolveDestination(
                "Sparta_056", 5053, 0, 0, 2, 251, empty, out _) &&
            !BattlefieldTransporterProtocol.TryResolveDestination(
                "Sparta_056", 5055, 0, 0, 1, 251, empty, out _) &&
            !BattlefieldTransporterProtocol.TryResolveDestination(
                "Sparta_056", 5053, 1, 0, 1, 251, empty, out _) &&
            !BattlefieldTransporterProtocol.TryResolveDestination(
                "Sparta_056", 5053, 0, 1, 1, 251, empty, out _) &&
            !BattlefieldTransporterProtocol.TryResolveDestination(
                "Sparta_056", 5053, 0, 0, 1, 252, empty, out _) &&
            !BattlefieldTransporterProtocol.TryResolveDestination(
                "Athens_056", 5195, 1, 1, 1, 251, empty, out _) &&
            !BattlefieldTransporterProtocol.TryResolveDestination(
                "Sparta_056", 5053, 0, 0, 1, 274, Arguments(272), out _),
            "wrong dialog, identity, faction, or unadvertised bracket fails closed");

        Check.True(
            !BattlefieldTransporterProtocol.TryResolveDestination(
                "Sparta_056", 5053, 0, 0, 1, 251, empty[..^1], out _) &&
            !BattlefieldTransporterProtocol.TryResolveDestination(
                "Sparta_056", 5053, 0, 0, 1, 251, [.. empty, -1], out _),
            "short and long Battlefield action paths fail closed");

        for (var index = 0;
             index < BattlefieldTransporterProtocol.FunctionArgumentCount;
             index++)
        {
            var polluted = Arguments();
            polluted[index] = 0;
            Check.True(
                !BattlefieldTransporterProtocol.TryResolveDestination(
                    "Sparta_056",
                    5053,
                    0,
                    0,
                    1,
                    1001,
                    polluted,
                    out _),
                $"Duel action argument {index} must remain empty");
        }

        var pollutedNested = Arguments(273);
        pollutedNested[1] = 0;
        Check.True(
            !BattlefieldTransporterProtocol.TryResolveDestination(
                "Sparta_056",
                5053,
                0,
                0,
                1,
                274,
                pollutedNested,
                out _),
            "Ni Mini accepts only its exact one-element nested path");
    }

    private static BattlefieldTransportDestination Resolve(
        string npcKey,
        uint npcId,
        byte sourceMapId,
        byte camp,
        int subId,
        params int[] path)
    {
        Check.True(
            BattlefieldTransporterProtocol.TryResolveDestination(
                npcKey,
                npcId,
                sourceMapId,
                camp,
                BattlefieldTransporterProtocol.DialogIndex,
                subId,
                Arguments(path),
                out var destination),
            $"Battlefield route {npcKey}/{subId} resolves");
        return destination;
    }

    private static void CheckDestination(
        BattlefieldTransportDestination destination,
        BattlefieldDestinationKind kind,
        int sourceMapId,
        int targetMapId,
        int minimumLevel,
        int maximumLevel)
    {
        var arrival = new MapTraversalPosition(
            destination.TargetX,
            destination.TargetZ);
        Check.True(
            destination.Kind == kind &&
            destination.SourceMapId == sourceMapId &&
            destination.TargetMapId == targetMapId &&
            destination.MinimumLevel == minimumLevel &&
            destination.MaximumLevel == maximumLevel &&
            MapTraversalLimits.IsFiniteAndBounded(arrival),
            $"{kind} has the expected map, level gate, and bounded arrival");
    }

    private static int[] Arguments(params int[] path)
    {
        var arguments = Enumerable.Repeat(
            -1,
            BattlefieldTransporterProtocol.FunctionArgumentCount).ToArray();
        path.CopyTo(arguments, 0);
        return arguments;
    }
}
