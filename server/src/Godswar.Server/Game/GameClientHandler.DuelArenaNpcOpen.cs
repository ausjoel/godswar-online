using Godswar.Server.Domain.World.Content;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private async Task<bool> TryHandleDuelArenaNpcDialogOpenAsync(
        GamePacket packet,
        NpcSpawnDefinition npc,
        CancellationToken cancellationToken)
    {
        if ((npc.MapId, npc.NpcKey, npc.InteractionId) is not
            (DuelArenaCapturedLayout.MapId,
                DuelArenaCapturedLayout.VendorNpcKey,
                DuelArenaCapturedLayout.VendorNpcId))
        {
            return false;
        }

        // The captured Vendor advertises only its description. No shop
        // inventory or economy action is implied by its appearance/name.
        if (packet.Length == 48 && packet.Buffer.Length == 48 &&
            CanUseCapturedArenaNpc(npc))
        {
            await _session.SendAsync(
                PacketBuilder.NpcDescriptionDialogOpenAck(
                    npc.InteractionId, npc.NpcKey),
                cancellationToken,
                "DuelArenaVendorDialogOpenAck");
        }
        return true;
    }

    private bool CanUseDuelArenaWarehouseNpc(NpcSpawnDefinition npc) =>
        !WarehouseNpcProtocol.IsDuelArenaWarehouseEndpoint(
            npc.NpcKey, npc.InteractionId) ||
        CanUseCapturedArenaNpc(npc);

    private bool CanUseCapturedArenaNpc(NpcSpawnDefinition npc) =>
        npc.MapId == DuelArenaCapturedLayout.MapId &&
        _character is { CurrentHp: > 0 } &&
        _character.CurrentMap == DuelArenaCapturedLayout.MapId &&
        HasCurrentWarehouseRealmAuthority() &&
        TryCaptureCurrentPlayerOwnership(out _) &&
        IsWithinDuelArenaTransporterInteractionDistance(npc);
}
