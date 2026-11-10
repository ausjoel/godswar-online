using Godswar.Server.Application.Characters;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.OnlineAwards;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Networking.Secure;
using Godswar.Server.Packets;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private async Task ReloadOnlineAwardProjectionAsync(
        PlayerOwnershipFence ownership,
        OnlineAwardExecutionReceipt receipt,
        CancellationToken cancellationToken)
    {
        var accountSnapshot = await _characterSnapshots.ReadAsync(
            _account!.Id,
            _processRealmId,
            cancellationToken);
        if (!RevalidateCurrentPlayerOwnership(ownership))
        {
            throw new InvalidOperationException(
                "The Online Award owner changed during projection reload.");
        }

        var persisted = accountSnapshot.Character;
        var hydrated = CharacterLoadSnapshotHydrator.Hydrate(accountSnapshot);
        if (persisted is null || hydrated is null ||
            hydrated.Character.Id != _character!.Id ||
            persisted.Identity.OnlineAwardRevision <
                receipt.OnlineAwardRevision ||
            persisted.Loadout.InventoryRevision <
                receipt.InventoryRevision)
        {
            throw new InvalidDataException(
                "The durable Online Award projection predates its receipt.");
        }

        _character.KitBag = hydrated.Character.KitBag;
        _character.OnlineAwardRevision =
            hydrated.Character.OnlineAwardRevision;
        _registry.UpdateCharacter(
            _session,
            _character,
            advanceWorldRevision: false);
        _pendingUnequipFollowup = null;
        ClearForgeSelection();
        ClearGearEnhancerSelection();
    }

    private void ValidateOnlineAwardReceipt(
        OnlineAwardExecutionReceipt receipt,
        OnlineAwardBalanceSnapshot? committedBalance)
    {
        if (_character is null ||
            receipt.CharacterId != _character.Id ||
            receipt.RealmId != _processRealmId.Value ||
            receipt.NativeResultSubId != OnlineAwardProtocol.SuccessSubId ||
            receipt.ClaimDay == default ||
            receipt.BalanceRevision <= 0 ||
            receipt.ItemDeltas is null ||
            receipt.ItemDeltas.Count is < 1 or >
                OnlineAwardBalanceSnapshot.MaximumRewardRows ||
            !IsOnlineAwardDigest(receipt.BalanceSha256) ||
            !IsOnlineAwardDigest(receipt.ItemContentRevision) ||
            receipt.ItemDeltas.Any(static delta =>
                delta.ItemId <= 0 ||
                delta.ItemQuality is < 1 or > 16 ||
                delta.Bound is < 0 or > 1 ||
                delta.Quantity is < 1 or >
                    OnlineAwardBalanceSnapshot.MaximumTotalQuantity) ||
            receipt.ItemDeltas.Sum(static delta => delta.Quantity) >
                OnlineAwardBalanceSnapshot.MaximumTotalQuantity ||
            receipt.InventoryRevision <= 0 ||
            receipt.OnlineAwardRevision <= 0 ||
            string.IsNullOrWhiteSpace(receipt.AuditId) ||
            receipt.EventId == Guid.Empty)
        {
            throw new InvalidDataException(
                "The Online Award receipt identity is inconsistent.");
        }

        if (committedBalance is null)
        {
            return;
        }
        var itemRevision = RequireItemContent().Templates.Revision.Sha256;
        if (receipt.BalanceRevision != committedBalance.Revision ||
            !string.Equals(
                receipt.BalanceSha256,
                committedBalance.Sha256,
                StringComparison.Ordinal) ||
            !string.Equals(
                receipt.ItemContentRevision,
                itemRevision,
                StringComparison.Ordinal) ||
            receipt.ItemDeltas.Count != committedBalance.Rewards.Count)
        {
            throw new InvalidDataException(
                "The committed Online Award receipt is not startup-pinned.");
        }

        for (var index = 0;
             index < committedBalance.Rewards.Count;
             index++)
        {
            var reward = committedBalance.Rewards[index];
            var delta = receipt.ItemDeltas[index];
            if (delta.ItemId != reward.ItemId ||
                delta.ItemQuality != reward.ItemQuality ||
                delta.Bound != reward.Bound ||
                delta.Quantity != reward.Quantity)
            {
                throw new InvalidDataException(
                    "The Online Award receipt changed a reward row.");
            }
        }
    }

    private async Task SendOnlineAwardDurableResultAsync(
        uint npcId,
        OnlineAwardOperationIdentity identity,
        OnlineAwardExecutionReceipt receipt,
        OnlineAwardExecutionDisposition disposition,
        string bagBefore,
        OnlineAwardBalanceSnapshot balance,
        CancellationToken cancellationToken)
    {
        foreach (var acknowledgement in
            PacketBuilder.KitBagMutationDeletionAcknowledgements(
                bagBefore,
                _character!.KitBag))
        {
            await _session.SendAsync(
                acknowledgement,
                cancellationToken,
                "OnlineAwardKitBagDeleteAck");
        }

        if (disposition == OnlineAwardExecutionDisposition.Committed)
        {
            var acquisitions = GetOnlineAwardAcquisitions(
                bagBefore,
                _character!.KitBag,
                balance);
            var scratchSlot = GetOnlineAwardAcquisitionScratchSlot(
                bagBefore,
                _character.KitBag);
            foreach (var acquisition in scratchSlot < 0
                         ? Enumerable.Empty<CompactItemEntry>()
                         : acquisitions)
            {
                await _session.SendAsync(
                    PacketBuilder.SystemAddItemWithAcquisitionLog(
                        acquisition),
                    cancellationToken,
                    "OnlineAwardAcquisition");
                await _session.SendAsync(
                    PacketBuilder.StorageItemKitBagDelete(scratchSlot),
                    cancellationToken,
                    "OnlineAwardAcquisitionCleanup");
            }
        }

        await SendKitBagRefreshAsync(cancellationToken);
        await _session.SendAsync(
            PacketBuilder.NpcFunctionActionResponse(
                npcId,
                OnlineAwardProtocol.DialogIndex,
                receipt.NativeResultSubId),
            cancellationToken,
            "OnlineAwardResult");
        if (identity.IsSecureClient)
        {
            await SendSecureGearMentorResultAsync(
                identity.OperationId,
                CommandFamily.OnlineAward,
                receipt.NativeResultSubId,
                disposition == OnlineAwardExecutionDisposition.Committed
                    ? SecureLegacyCommandDisposition.Applied
                    : SecureLegacyCommandDisposition.Replayed,
                receipt.OnlineAwardRevision,
                cancellationToken);
        }
    }

    internal static IReadOnlyList<CompactItemEntry>
        GetOnlineAwardAcquisitions(
        string bagBefore,
        string bagAfter,
        OnlineAwardBalanceSnapshot balance)
    {
        ArgumentNullException.ThrowIfNull(balance);
        balance.Validate();
        var before = CountOnlineAwardItems(bagBefore, null);
        var representatives = new Dictionary<
            (uint, short, short), CompactItemEntry>();
        var after = CountOnlineAwardItems(bagAfter, representatives);
        var result = new List<CompactItemEntry>();
        foreach (var reward in balance.Rewards.OrderBy(
                     static value => value.Order))
        {
            var key = (
                checked((uint)reward.ItemId),
                reward.ItemQuality,
                reward.Bound);
            before.TryGetValue(key, out var oldQuantity);
            after.TryGetValue(key, out var newQuantity);
            if (newQuantity - oldQuantity != reward.Quantity ||
                !representatives.TryGetValue(key, out var item))
            {
                throw new InvalidDataException(
                    "The Online Award bag delta differs from its receipt.");
            }

            var remaining = reward.Quantity;
            while (remaining > 0)
            {
                var quantity = checked((short)Math.Min(
                    remaining,
                    Math.Min(reward.StackCap, sbyte.MaxValue)));
                result.Add(item with { Stack = quantity });
                remaining -= quantity;
            }
        }
        return result;
    }

    internal static int GetOnlineAwardAcquisitionScratchSlot(
        string bagBefore,
        string bagAfter)
    {
        for (var slot = 0; slot < KitBagItemGrantPlanner.SlotCount; slot++)
        {
            var previous = KitBagSlots.GetItem(bagBefore, slot);
            if (previous.IsEmpty ||
                previous != KitBagSlots.GetItem(bagAfter, slot))
            {
                // Mutation deletion ACKs run first, so both an original empty
                // slot and a changed occupied slot are transiently available.
                return slot;
            }
        }
        return -1;
    }

    private static bool IsOnlineAwardDigest(string? value) =>
        value is { Length: 64 } && value.All(static character =>
            character is >= '0' and <= '9' or >= 'A' and <= 'F');

    private static Dictionary<(uint, short, short), int>
        CountOnlineAwardItems(
        string kitBag,
        Dictionary<(uint, short, short), CompactItemEntry>?
            representatives)
    {
        var counts = new Dictionary<(uint, short, short), int>();
        for (var slot = 0; slot < KitBagItemGrantPlanner.SlotCount; slot++)
        {
            var item = KitBagSlots.GetItem(kitBag, slot);
            if (item.IsEmpty || item.Stack <= 0)
            {
                continue;
            }
            var key = (item.Id, item.Quality, item.Bound);
            counts.TryGetValue(key, out var quantity);
            counts[key] = checked(quantity + item.Stack);
            if (representatives is not null)
            {
                representatives[key] = item;
            }
        }
        return counts;
    }
}
