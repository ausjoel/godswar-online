using System.Buffers.Binary;
using Godswar.Server.Application.Characters;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private WonderlandTransportDialogContext? _wonderlandTransportDialog;

    private sealed record WonderlandTransportDialogContext(uint NpcId, int CharacterId,
        WorldInstanceId InstanceId, PlayerOwnershipFence Ownership, long LifeRevision, DateTimeOffset ExpiresAt);

    private async Task<bool> TryHandleWonderlandNpcOpenAsync(GamePacket packet, NpcSpawnDefinition npc,
        CancellationToken cancellationToken)
    {
        var blackmarket = WonderlandTransportProtocol.IsBlackmarket(npc);
        if (_character?.CurrentMap != 207 ||
            !blackmarket && !WonderlandTraversalPolicy.TryGetIsland(npc, out _)) return false;
        if (packet.Length != 48 || packet.Buffer.Length != 48 ||
            !CanInteractWithWonderlandTransport(npc) || !TryCaptureCurrentPlayerOwnership(out var ownership) ||
            !_registry.TryCaptureWonderlandNpcInteraction(_session, out var instanceId) ||
            !_registry.TryGetPlayerLifeRevision(_session, out var lifeRevision)) return true;
        _wonderlandTransportDialog = new(npc.InteractionId, _character.Id, instanceId, ownership,
            lifeRevision, DateTimeOffset.UtcNow + WonderlandTransportProtocol.DialogLifetime);
        await _session.SendAsync(PacketBuilder.NpcDialogOpenAck(npc.InteractionId,
                blackmarket ? WonderlandTransportProtocol.BlackmarketDialogIndices :
                    new[] { WonderlandTransportProtocol.TeleportDialogIndex }, npc.NpcKey),
            cancellationToken, "WonderlandTransportDialogOpenAck");
        return true;
    }

    private bool CanInteractWithWonderlandTransport(NpcSpawnDefinition npc)
    {
        if (_character?.CurrentMap != 207 || _character.CurrentHp <= 0) return false;
        var dx = (double)_character.PositionX - npc.X;
        var dz = (double)_character.PositionZ - npc.Z;
        var distanceSquared = dx * dx + dz * dz;
        return double.IsFinite(distanceSquared) && distanceSquared <=
            WonderlandTransportProtocol.InteractionRadius * WonderlandTransportProtocol.InteractionRadius;
    }

    private async Task<bool> TryHandleWonderlandTransportActionAsync(GamePacket packet, uint npcId,
        int dialogIndex, int subId, CancellationToken cancellationToken)
    {
        if (_character?.CurrentMap != 207 || !TryResolveMapNpc(npcId, out var npc)) return false;
        var blackmarket = WonderlandTransportProtocol.IsBlackmarket(npc);
        var teleporter = WonderlandTraversalPolicy.TryGetIsland(npc, out var island);
        if (!blackmarket && !teleporter) return false;
        var allowedDialog = blackmarket
            ? WonderlandTransportProtocol.BlackmarketDialogIndices.Contains(dialogIndex)
            : dialogIndex == WonderlandTransportProtocol.TeleportDialogIndex;
        // Only the initialized native header is an action. The remaining
        // request argument tail contains client stack bytes and is ignored.
        if (!allowedDialog || packet.Length != 92 || packet.Buffer.Length != 92 || subId != -1 ||
            BinaryPrimitives.ReadInt32LittleEndian(packet.Buffer.AsSpan(12)) != dialogIndex ||
            _wonderlandTransportDialog is not { } context || context.NpcId != npcId ||
            context.CharacterId != _character.Id || context.ExpiresAt <= DateTimeOffset.UtcNow ||
            !RevalidateCurrentPlayerOwnership(context.Ownership) || !CanInteractWithWonderlandTransport(npc) ||
            !_registry.TryGetPlayerLifeRevision(_session, out var lifeRevision) || lifeRevision != context.LifeRevision ||
            !_registry.TryCaptureWonderlandNpcInteraction(_session, out var instanceId) || instanceId != context.InstanceId)
            return true;
        _wonderlandTransportDialog = null;
        if (blackmarket)
        {
            await HandleWonderlandBlackmarketActionAsync(npc, dialogIndex, instanceId, context.Ownership,
                cancellationToken);
            return true;
        }
        if (island == 8)
        {
            await HandleWonderlandFinalExitAsync(instanceId, context.LifeRevision, cancellationToken);
            return true;
        }
        if (!await TravelWonderlandIslandAsync(island, cancellationToken))
            await _session.SendAsync(PacketBuilder.CapturedNpcFunctionActionResponse(npcId,
                    WonderlandTransportProtocol.TeleportDialogIndex, [0, 99 + island]),
                cancellationToken, "WonderlandIslandLocked");
        return true;
    }
}
