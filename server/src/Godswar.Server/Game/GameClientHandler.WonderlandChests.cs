using System.Buffers.Binary;
using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private (uint NpcId, WorldInstanceId InstanceId, int CharacterId)? _wonderlandChestDialog;

    private async Task<bool> TryHandleWonderlandChestOpenAsync(GamePacket packet, NpcSpawnDefinition npc,
        CancellationToken cancellationToken)
    {
        if (_character?.CurrentMap != 207 || !WonderlandTreasureChestPolicy.TryGetIsland(npc, out _)) return false;
        if (_account is null || packet.Length != 48 || packet.Buffer.Length != 48 ||
            !CanInteractWithWonderlandChest(npc) ||
            !_registry.TryGetSessionWorldInstanceId(_session, out var instanceId)) return true;
        _wonderlandChestDialog = (npc.InteractionId, instanceId, _character.Id);
        await _session.SendAsync(PacketBuilder.NpcDialogOpenAck(npc.InteractionId,
            WonderlandTreasureChestPolicy.DialogIndex, npc.NpcKey), cancellationToken,
            "WonderlandTreasureDialogOpenAck");
        return true;
    }

    private bool CanInteractWithWonderlandChest(NpcSpawnDefinition npc)
    {
        if (_character?.CurrentMap != 207 || _character.CurrentHp <= 0 ||
            !TryCaptureCurrentPlayerOwnership(out _)) return false;
        var dx = (double)_character.PositionX - npc.X;
        var dz = (double)_character.PositionZ - npc.Z;
        var distanceSquared = dx * dx + dz * dz;
        return double.IsFinite(distanceSquared) && distanceSquared <=
            WonderlandTreasureChestPolicy.InteractionRadius * WonderlandTreasureChestPolicy.InteractionRadius;
    }

    private async Task<bool> TryHandleWonderlandChestClaimAsync(GamePacket packet, uint npcId,
        int dialogIndex, int subId, CancellationToken cancellationToken)
    {
        if (_character?.CurrentMap != 207 || !TryResolveMapNpc(npcId, out var npc) ||
            !WonderlandTreasureChestPolicy.TryGetIsland(npc, out _)) return false;
        // Only the captured claim action is authoritative. The remaining native
        // 92-byte request tail contains uninitialized client memory, not rewards.
        if (_account is null || packet.Length != 92 || packet.Buffer.Length != 92 ||
            dialogIndex != WonderlandTreasureChestPolicy.DialogIndex || subId != -1 ||
            BinaryPrimitives.ReadInt32LittleEndian(packet.Buffer.AsSpan(12)) != dialogIndex ||
            _wonderlandChestDialog is not { } context || context.NpcId != npcId ||
            context.CharacterId != _character.Id ||
            !_registry.IsSessionInWorldInstance(_session, context.InstanceId) ||
            !CanInteractWithWonderlandChest(npc) ||
            !TryCaptureCurrentPlayerOwnership(out var ownership)) return true;
        var bagBefore = _character.KitBag;
        var receipt = await _registry.ClaimWonderlandChestAsync(_session, npc, DateTimeOffset.UtcNow, cancellationToken);
        if (!RevalidateCurrentPlayerOwnership(ownership) ||
            !_registry.IsSessionInWorldInstance(_session, context.InstanceId)) return true;
        if (receipt.Succeeded)
        {
            var snapshot = await _characterSnapshots.ReadAsync(_account.Id, _processRealmId, cancellationToken);
            if (!RevalidateCurrentPlayerOwnership(ownership) ||
                !_registry.IsSessionInWorldInstance(_session, context.InstanceId)) return true;
            var hydrated = CharacterLoadSnapshotHydrator.Hydrate(snapshot);
            if (snapshot.Character is null || hydrated is null || hydrated.Character.Id != _character.Id ||
                snapshot.Character.Loadout.InventoryRevision < receipt.InventoryRevision)
                throw new InvalidDataException("Wonderland chest projection predates its committed receipt.");
            _character.KitBag = hydrated.Character.KitBag;
            _registry.UpdateCharacter(_session, _character, advanceWorldRevision: false);
            await SendWonderlandRewardProjectionAsync(bagBefore,
                receipt.Status == WonderlandChestClaimStatus.Claimed ? receipt.Rewards : [], cancellationToken);
            return true;
        }
        if (receipt.Status == WonderlandChestClaimStatus.NotEligible)
        {
            // Same native log destination as daily item gains, with text only.
            // The claim click has already closed its initial NPC window.
            await _session.SendAsync(PacketBuilder.PersonalGameLog(
                "Defeat this island's required enemies before opening its treasure chest."),
                cancellationToken, "WonderlandTreasureLockedLog");
            return true;
        }
        await _session.SendAsync(PacketBuilder.CapturedNpcFunctionActionResponse(npcId,
                WonderlandTreasureChestPolicy.DialogIndex, WonderlandChestResultSubIds(receipt)),
            cancellationToken, "WonderlandTreasureClaimResult");
        var message = receipt.Status switch
        {
            WonderlandChestClaimStatus.InventoryFull => "Make room in your bag, then click the treasure chest again.",
            _ => "Your treasure is not ready yet. Please try again shortly."
        };
        await _session.SendAsync(PacketBuilder.ServerNote(message), cancellationToken, "WonderlandTreasureClaim");
        return true;
    }

    private static int[] WonderlandChestResultSubIds(WonderlandChestClaimReceipt receipt) => receipt.Status switch
    {
        WonderlandChestClaimStatus.InventoryFull => [0, 123],
        _ => [0, 150]
    };
}
