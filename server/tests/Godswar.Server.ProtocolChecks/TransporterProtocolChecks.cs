using Godswar.Server.Domain.World.Content;
using Godswar.Server.Game;

namespace Godswar.Server.ProtocolChecks;

internal static class TransporterProtocolChecks
{
    public const string CheckName =
        "Ordinary Transporter endpoint and route protocol";

    public static Task RunAsync()
    {
        CheckWireContract();
        CheckEndpointMenus();
        CheckExtensionMenus();
        CheckDestinations();
        CheckSafeArrivals();
        CheckStrictActionPath();
        CheckRouteExclusions();
        return Task.CompletedTask;
    }

    private static void CheckWireContract()
    {
        Check.Equal(1, TransporterProtocol.DialogIndex,
            "ordinary Transporters use NpcFunTranmit dialog 1");
        Check.Equal(92, TransporterProtocol.ActionPacketBytes,
            "ordinary Transporter actions use the stock 92-byte frame");
        Check.Equal(18, TransporterProtocol.FunctionArgumentCount,
            "ordinary Transporter actions contain 18 function arguments");
        Check.Equal(-1, TransporterProtocol.InitialRequestSubId,
            "ordinary Transporter initial menus use sub-ID -1");
    }

    private static void CheckEndpointMenus()
    {
        (string Key, uint Id, int[] Menu)[] endpoints =
        [
            ("Sparta_042", 5039, [1, 3, 2, 8, 9, 10]),
            ("Athens_041", 5179, [4, 6, 5, 7, 9, 10]),
            ("Mycenae_All_013", 59721, [1088, 1089, 1090])
        ];

        foreach (var (key, id, expectedMenu) in endpoints)
        {
            Check.True(
                TransporterProtocol.IsEndpoint(key, id) &&
                TransporterProtocol.TryGetInitialMenu(
                    key,
                    id,
                    out var actualMenu) &&
                actualMenu.SequenceEqual(expectedMenu),
                $"ordinary Transporter {key}/{id} publishes its exact menu");
        }

        Check.True(
            !TransporterProtocol.IsEndpoint("Sparta_042", 5180) &&
            !TransporterProtocol.IsEndpoint("Athens_041", 5039) &&
            !TransporterProtocol.IsEndpoint("Mycenae_All_013", 5039) &&
            !TransporterProtocol.TryGetInitialMenu(
                "Sparta_041",
                5039,
                out var unrelatedMenu) &&
            unrelatedMenu.Count == 0,
            "mixed and unrelated identities cannot acquire Transporter menus");
    }

    private static void CheckExtensionMenus()
    {
        Check.True(
            TransporterProtocol.GetExtensionMenu("Sparta_042", 5039).SequenceEqual([11, 1089, 1088, 1091, 1090, 1092]) &&
            TransporterProtocol.GetExtensionMenu("Athens_041", 5179).SequenceEqual([11, 1089, 1088, 1091, 1090, 1092]) &&
            TransporterProtocol.GetExtensionMenu("Athens_041", 5180).SequenceEqual([11, 1089, 1088, 1091, 1090, 1092]) &&
            TransporterProtocol.GetExtensionMenu("Mycenae_All_013", 59721).SequenceEqual([1091, 1092, 1093]) &&
            TransporterProtocol.GetExtensionMenu("Sparta_042", 5180).Count == 0,
            "the capital and Mycenae Transporters append their north entries");
    }

    private static void CheckDestinations()
    {
        (string Key, uint Id, short Source, int SubId, short Target,
            int MinimumLevel)[] expected =
        [
            ("Sparta_042", 5039, 0, 1, 4, 1),
            ("Sparta_042", 5039, 0, 2, 5, 40),
            ("Sparta_042", 5039, 0, 3, 13, 30),
            ("Sparta_042", 5039, 0, 8, 14, 70),
            ("Sparta_042", 5039, 0, 9, 8, 100),
            ("Sparta_042", 5039, 0, 10, 9, 130),
            ("Athens_041", 5179, 1, 4, 2, 1),
            ("Athens_041", 5179, 1, 5, 3, 40),
            ("Athens_041", 5179, 1, 6, 11, 30),
            ("Athens_041", 5179, 1, 7, 12, 70),
            ("Athens_041", 5179, 1, 9, 8, 100),
            ("Athens_041", 5179, 1, 10, 9, 130),
            ("Sparta_042", 5039, 0, 11, 6, 130),
            ("Athens_041", 5179, 1, 11, 6, 130),
            ("Sparta_042", 5039, 0, 1088, 7, 145),
            ("Athens_041", 5179, 1, 1088, 7, 145),
            ("Sparta_042", 5039, 0, 1089, 20, 135),
            ("Sparta_042", 5039, 0, 1091, 15, 100), // FINALISE2 (F-20): was 155
            ("Sparta_042", 5039, 0, 1090, 10, 165),
            ("Sparta_042", 5039, 0, 1092, 22, 175),
            ("Athens_041", 5179, 1, 1089, 20, 135),
            ("Athens_041", 5179, 1, 1091, 15, 100), // FINALISE2 (F-20): was 155
            ("Athens_041", 5179, 1, 1090, 10, 165),
            ("Athens_041", 5179, 1, 1092, 22, 175),
            ("Mycenae_All_013", 59721, 6, 1091, 15, 100), // FINALISE2 (F-20): was 155
            ("Mycenae_All_013", 59721, 6, 1092, 22, 175),
            ("Mycenae_All_013", 59721, 6, 1093, 21, 185),
            ("Mycenae_All_013", 59721, 6, 1088, 7, 145),
            ("Mycenae_All_013", 59721, 6, 1089, 20, 135),
            ("Mycenae_All_013", 59721, 6, 1090, 10, 165)
        ];

        var emptyPath = EmptyPath();
        foreach (var route in expected)
        {
            Check.True(
                TransporterProtocol.TryResolveDestination(
                    route.Key,
                    route.Id,
                    route.Source,
                    TransporterProtocol.DialogIndex,
                    route.SubId,
                    emptyPath,
                    out var destination) &&
                destination.TargetMapId == route.Target &&
                destination.MinimumLevel == route.MinimumLevel,
                $"Transporter route {route.Key}/{route.SubId} resolves to " +
                $"map {route.Target} at level {route.MinimumLevel}");
        }

        Check.Equal(expected.Length, TransporterProtocol.Destinations.Count,
            "ordinary Transporter catalog contains only authored routes");
    }

    private static void CheckStrictActionPath()
    {
        var valid = EmptyPath();
        Check.True(
            TransporterProtocol.TryResolveDestination(
                "Sparta_042",
                5039,
                0,
                TransporterProtocol.DialogIndex,
                1,
                valid,
                out _),
            "an exact empty 18-argument path resolves");

        Check.True(
            !TransporterProtocol.TryResolveDestination(
                "Sparta_042", 5039, 0, 1, 1, valid[..^1], out _) &&
            !TransporterProtocol.TryResolveDestination(
                "Sparta_042", 5039, 0, 1, 1, [.. valid, -1], out _),
            "short and long action paths fail closed");

        for (var index = 0;
             index < TransporterProtocol.FunctionArgumentCount;
             index++)
        {
            var nonEmpty = EmptyPath();
            nonEmpty[index] = 0;
            Check.True(
                !TransporterProtocol.TryResolveDestination(
                    "Sparta_042", 5039, 0, 1, 1, nonEmpty, out _),
                $"non-empty action argument {index} fails closed");
        }
    }

    private static void CheckSafeArrivals()
    {
        const float portalRadius = 6f;
        var traversal = GameplayContentTestFixtures.Runtime.MapTraversal;
        foreach (var destination in TransporterProtocol.Destinations)
        {
            Check.True(
                traversal.TryGetAutomaticLink(
                    destination.ArrivalAnchorSourceMapId,
                    destination.TargetMapId,
                    out var entryAnchor) &&
                traversal.TryResolveTargetArrival(
                    entryAnchor,
                    portalRadius,
                    out var arrival) &&
                arrival.SourceMapId ==
                    destination.ArrivalAnchorSourceMapId &&
                arrival.TargetMapId == destination.TargetMapId &&
                MapTraversalLimits.IsFiniteAndBounded(
                    arrival.TargetArrival),
                $"Transporter {destination.NpcKey}/{destination.SubId} " +
                $"resolves a safe arrival on map " +
                $"{destination.TargetMapId}");
        }
    }

    private static void CheckRouteExclusions()
    {
        var emptyPath = EmptyPath();
        Check.True(
            !TransporterProtocol.TryResolveDestination(
                "Sparta_042", 5039, 0, 2, 1, emptyPath, out _) &&
            !TransporterProtocol.TryResolveDestination(
                "Sparta_042", 5039, 1, 1, 1, emptyPath, out _) &&
            !TransporterProtocol.TryResolveDestination(
                "Athens_041", 5179, 1, 1, 1, emptyPath, out _) &&
            !TransporterProtocol.TryResolveDestination(
                "Sparta_042", 5039, 1, 1, 11, emptyPath, out _) &&
            !TransporterProtocol.TryResolveDestination(
                "Athens_041", 5179, 0, 1, 11, emptyPath, out _) &&
            !TransporterProtocol.TryResolveDestination(
                "Mycenae_All_013", 59721, 6, 1, 11, emptyPath, out _),
            "wrong dialogs, maps, endpoints, and sub-ID 11 outside the capitals fail closed");
    }

    private static int[] EmptyPath() =>
        Enumerable.Repeat(
            -1,
            TransporterProtocol.FunctionArgumentCount).ToArray();
}
