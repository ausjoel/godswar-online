using Godswar.Server.Application.Commands;

namespace Godswar.Server.Application.Pets;

internal sealed partial record PetDurableReceipt
{
    private bool MatchesGiftPackEvidence() => Status == PetDurableReceiptStatus.GiftPackOpened
        ? Family == CommandFamily.BagItemActivation && EquipmentSlot == -1 && PetId == 0 && PetRevision == 0 &&
            GiftPack is { IsValid: true } evidence && evidence.KitBagSlot == KitBagSlot
        : GiftPack is null && (Status != PetDurableReceiptStatus.GiftPackBagFull ||
            Family == CommandFamily.BagItemActivation && KitBagSlot is >= 0 and < 96 && EquipmentSlot == -1 && PetId == 0);
}
