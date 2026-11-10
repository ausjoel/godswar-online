using System.Buffers.Binary;
using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private async Task<bool> TryHandleWonderlandNativeLootPickupAsync(GamePacket packet, CancellationToken token)
    {
        // Origin's loot click/Take All sends10050,20 bytes. +8 and bytes17..19
        // are uninitialized native padding, not a player ID or inventory slot.
        if (_character?.CurrentMap != 207 || packet.Length != 20 || packet.Buffer.Length != 20 ||
            packet.Buffer[16] != 0) return false;
        var objectId = BinaryPrimitives.ReadUInt32LittleEndian(packet.Buffer.AsSpan(4));
        var pickupIndex = BinaryPrimitives.ReadInt32LittleEndian(packet.Buffer.AsSpan(12));
        if (!_registry.TryResolveWonderlandBossLootPickup(_session, objectId, pickupIndex,
                DateTimeOffset.UtcNow, out _)) return false;
        await HandleWonderlandBossLootPickupAsync(objectId, pickupIndex, token);
        return true;
    }

    private async Task HandleWonderlandBossLootPickupAsync(uint objectId, int pickupIndex, CancellationToken token)
    {
        if (_account is null || _character is null || !TryCaptureCurrentPlayerOwnership(out var ownership) ||
            !_registry.TryResolveWonderlandBossLootPickup(_session, objectId, pickupIndex, DateTimeOffset.UtcNow, out var request)) return;
        var bagBefore = _character.KitBag;
        var receipt = await _registry.ClaimWonderlandBossLootAsync(request, token);
        if (!RevalidateCurrentPlayerOwnership(ownership) || !_registry.IsSessionInWorldInstance(_session, request.WorldInstanceId)) return;
        if (receipt.Succeeded)
        {
            var snapshot = await _characterSnapshots.ReadAsync(_account.Id, _processRealmId, token);
            if (!RevalidateCurrentPlayerOwnership(ownership) || !_registry.IsSessionInWorldInstance(_session, request.WorldInstanceId)) return;
            var hydrated = CharacterLoadSnapshotHydrator.Hydrate(snapshot);
            if (snapshot.Character is null || hydrated is null || hydrated.Character.Id != _character.Id ||
                snapshot.Character.Loadout.InventoryRevision < receipt.InventoryRevision)
                throw new InvalidDataException("Wonderland boss loot projection predates its committed receipt.");
            // Native local-player 10050 chooses a free bag slot and adds the
            // corpse item. 10033 skips empty records, so a later snapshot cannot
            // remove that optimistic item if the durable award uses another slot.
            // Acquisition notices clean their temporary slot before hydration;
            // only the durable award remains. The claimed presentation below
            // clears the loot panel through the bag-neutral observer ACK.
            _character.KitBag = hydrated.Character.KitBag;
            _registry.UpdateCharacter(_session, _character, advanceWorldRevision: false);
            await SendWonderlandRewardProjectionAsync(bagBefore,
                receipt.Status == WonderlandChestClaimStatus.Claimed ? receipt.Rewards : [], token);
            _registry.MarkWonderlandBossLootClaimed(_session, request.WorldInstanceId, objectId, request.SpawnGeneration);
            await _registry.RefreshWonderlandBossLootAsync(_session, token);
        }
        else
        {
            var message = receipt.Status == WonderlandChestClaimStatus.InventoryFull
                ? "Make room in your bag and loot the boss before its corpse disappears."
                : "This boss's loot is not available. Please try again shortly.";
            await _session.SendAsync(PacketBuilder.ServerNote(message), token, "WonderlandBossLootResult");
        }
        Console.WriteLine($"[wonderland-loot] character={_character.Name} boss={objectId} sack={request.SackItemId} status={receipt.Status}");
    }
}
