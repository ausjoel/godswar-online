using Godswar.Server.Application.Pets;
using Godswar.Server.Packets;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private bool IsMoneyBagProjection(PetDurableReceipt receipt) =>
        receipt.Status is PetDurableReceiptStatus.MoneyBagOpened or PetDurableReceiptStatus.MoneyBagWalletFull ||
        receipt.Status == PetDurableReceiptStatus.ConsumableCooldownActive && _character is not null &&
        receipt.KitBagSlot is >= 0 and < 96 && MoneyBagPolicy.TryResolve(RequireItemContent().Templates,
            KitBagSlots.GetItemId(_character.KitBag, receipt.KitBagSlot), out _);

    private async Task<bool> SendMoneyBagProjectionAsync(PetDurableReceipt receipt,
        PetDurableExecutionDisposition disposition, string previousKitBag, CancellationToken token)
    {
        if (_character is null) return false;
        var firstCommit = disposition == PetDurableExecutionDisposition.Committed;
        if (receipt.Status == PetDurableReceiptStatus.MoneyBagOpened)
        {
            if (receipt.MoneyBag is not { IsValid: true } evidence) return false;
            // Never replay a historical delta: duplicates get the current wallet/bag snapshot only.
            if (firstCommit)
            {
                await _session.SendAsync(PacketBuilder.BagSilverGrant(LocalPlayerObjectId, evidence.Amount),
                    token, "MoneyBagSilverGrant");
                await SendGainLogAsync($"Obtained {Amount(evidence.Amount)} Silver", token); // FINALISE
                if (KitBagSlots.GetItemId(previousKitBag, receipt.KitBagSlot) == evidence.ItemTemplateId)
                    await _session.SendAsync(PacketBuilder.BagConsumableActivation(previousKitBag,
                        receipt.KitBagSlot, LocalPlayerObjectId), token, "MoneyBagActivation");
            }
            if (KitBagSlots.GetItem(_character.KitBag, receipt.KitBagSlot).IsEmpty)
                await _session.SendAsync(PacketBuilder.StorageItemKitBagDelete(receipt.KitBagSlot), token,
                    "MoneyBagConsumedSlotClear");
        }
        await SendKitBagRefreshAsync(token);
        await _session.SendAsync(BuildLocalPlayerStatusUpdate(), token, "MoneyBagCurrentWallet");
        return true;
    }
}
