using Godswar.Server.Domain.World.Content;
using Godswar.Server.Protocol;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private async Task<bool> TryHandleNpcInitialActionAsync(
        GamePacket packet,
        NpcSpawnDefinition npc,
        NpcDialogueRouteDefinition route,
        uint npcId,
        int dialogIndex,
        int subId,
        IReadOnlyList<int> arguments,
        CancellationToken cancellationToken)
    {
        if (route.Behavior == NpcDialogueBehavior.OnlineAward)
        {
            await HandleOnlineAwardAsync(packet, route, npcId, dialogIndex,
                subId, arguments, cancellationToken);
        }
        else if (DuelArenaCapturedTransportProtocol.IsCapturedRoute(route))
        {
            await HandleDuelArenaTransporterAsync(packet, route, npcId,
                dialogIndex, subId, arguments, cancellationToken);
        }
        else if (route.Behavior == NpcDialogueBehavior.DuelArenaServices)
        {
            await HandleDuelArenaServiceAsync(packet, npc, route, dialogIndex,
                subId, arguments, cancellationToken);
        }
        else if (subId == -1)
        {
            await SendNpcInitialMenuAsync(npc, route, cancellationToken);
        }
        else
        {
            return false;
        }
        return true;
    }
}
