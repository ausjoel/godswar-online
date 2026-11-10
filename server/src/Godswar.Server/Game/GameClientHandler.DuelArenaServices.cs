using System.Buffers.Binary;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private async Task HandleDuelArenaServiceAsync(
        GamePacket packet,
        NpcSpawnDefinition npc,
        NpcDialogueRouteDefinition route,
        int dialogIndex,
        int subId,
        IReadOnlyList<int> arguments,
        CancellationToken cancellationToken)
    {
        if (!CanUseCapturedArenaNpc(npc) ||
            !DuelArenaServiceProtocol.IsAllowed(npc, route) ||
            packet.Length != DuelArenaTransporterProtocol.ActionPacketBytes ||
            packet.Buffer.Length != DuelArenaTransporterProtocol.ActionPacketBytes ||
            dialogIndex != route.DialogIndex ||
            BinaryPrimitives.ReadInt32LittleEndian(packet.Buffer.AsSpan(12)) != dialogIndex)
        {
            return;
        }

        if (DuelArenaServiceProtocol.IsInitialRequest(subId, arguments))
        {
            if (dialogIndex == DuelArenaServiceProtocol.PhysicianDialogIndex)
            {
                await _session.SendAsync(
                    PacketBuilder.CapturedNpcFunctionActionResponse(
                        npc.InteractionId, dialogIndex,
                        DuelArenaServiceProtocol.PhysicianInitialMenu.ToArray()),
                    cancellationToken, "DuelArenaPhysicianMenu");
            }
            else if (DuelArenaExitProtocol.IsRoute(route))
            {
                await HandleDuelArenaExitAsync(route, npc.InteractionId, cancellationToken);
            }
            // The captured AirDrop initial request received no menu response.
            return;
        }

        if (dialogIndex == DuelArenaServiceProtocol.PhysicianDialogIndex &&
            subId is 100 or 101 or 102)
        {
            await _session.SendAsync(
                PacketBuilder.ServerNote("Treatment is temporarily unavailable."),
                cancellationToken, "DuelArenaTreatmentUnavailable");
        }
    }
}
