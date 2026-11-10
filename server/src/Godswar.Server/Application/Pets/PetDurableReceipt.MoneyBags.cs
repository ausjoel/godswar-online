using Godswar.Server.Application.Commands;

namespace Godswar.Server.Application.Pets;

internal sealed partial record PetDurableReceipt
{
    private bool MatchesMoneyBagEvidence() => Status == PetDurableReceiptStatus.MoneyBagOpened
        ? Family == CommandFamily.BagItemActivation && EquipmentSlot == -1 && PetId == 0 && PetRevision == 0 &&
            MoneyBag is { IsValid: true } evidence && evidence.KitBagSlot == KitBagSlot
        : MoneyBag is null && (Status != PetDurableReceiptStatus.MoneyBagWalletFull ||
            Family == CommandFamily.BagItemActivation && KitBagSlot is >= 0 and < 96 && EquipmentSlot == -1 && PetId == 0);
}
