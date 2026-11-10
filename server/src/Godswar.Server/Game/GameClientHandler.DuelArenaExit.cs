using Godswar.Server.Domain.World.Content;
using Godswar.Server.Packets;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private async Task HandleDuelArenaExitAsync(
        NpcDialogueRouteDefinition route,
        uint npcId,
        CancellationToken cancellationToken)
    {
        if (_character is null || !DuelArenaExitProtocol.IsRoute(route) ||
            !DuelArenaExitProtocol.IsEndpoint(route.NpcKey, npcId) ||
            !TryAuthorizeDuelArenaTransporterAction(route, npcId) ||
            !DuelArenaExitProtocol.TryResolveDestination(_character.Camp, out var destination))
        {
            return;
        }

        ClearDuelArenaTransporterDialogueContext();
        var outcome = await TryBeginMapTransitionAsync(destination.MapId,
                destination.X, destination.Z, "npc-duel-arena-doorkeeper-exit",
                cancellationToken);
        if (outcome == SceneTransitionOutcome.RejectedWithoutRelocation &&
            !_session.IsDisconnected)
        {
            await _session.SendAsync(
                PacketBuilder.ServerNote("Leaving the Duel Arena is temporarily unavailable."),
                cancellationToken, "DuelArenaExitUnavailable");
        }
    }
}
