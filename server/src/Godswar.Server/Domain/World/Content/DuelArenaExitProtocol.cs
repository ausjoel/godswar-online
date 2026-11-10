namespace Godswar.Server.Domain.World.Content;

internal sealed record DuelArenaExitDestination(byte MapId, float X, float Z);

/// <summary>
/// Doorkeeper exit observed at 17:29:13 on September 7. The native initial
/// leave action returns to the faction capital, outside the Arena map.
/// </summary>
internal static class DuelArenaExitProtocol
{
    public const float CapitalArrivalX = 20.031299591064453f;
    public const float CapitalArrivalZ = -100.11289978027344f;

    public static bool IsEndpoint(string npcKey, uint npcId) =>
        npcKey == DuelArenaCapturedLayout.DoorkeeperNpcKey &&
        npcId == DuelArenaCapturedLayout.DoorkeeperNpcId;

    public static bool IsRoute(NpcDialogueRouteDefinition route) =>
        route.NpcKey == DuelArenaCapturedLayout.DoorkeeperNpcKey &&
        DuelArenaServiceProtocol.IsCapturedRoute(route);

    public static bool TryResolveDestination(short camp, out DuelArenaExitDestination destination)
    {
        destination = camp switch
        {
            0 => new(0, CapitalArrivalX, CapitalArrivalZ),
            // Athens and Sparta share byte-identical terrain. This is the
            // faction counterpart of the captured Sparta arrival, not a
            // separately observed Athens exit.
            1 => new(1, CapitalArrivalX, CapitalArrivalZ),
            _ => null!
        };
        return destination is not null;
    }
}
