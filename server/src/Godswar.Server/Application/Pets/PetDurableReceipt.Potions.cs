using Godswar.Server.Application.Commands;

namespace Godswar.Server.Application.Pets;

internal sealed partial record PetDurableReceipt
{
    private bool MatchesPotionEvidence() => Status == PetDurableReceiptStatus.PotionUsed
        ? Family == CommandFamily.BagItemActivation && EquipmentSlot == -1 && PetId == 0 && PetRevision == 0 &&
            Potion is { IsValid: true } evidence && evidence.KitBagSlot == KitBagSlot
        : Potion is null && (Status is not (PetDurableReceiptStatus.PotionNotNeeded or
            PetDurableReceiptStatus.PotionContextMismatch) ||
            Family == CommandFamily.BagItemActivation && KitBagSlot is >= 0 and < 96 && EquipmentSlot == -1 && PetId == 0);
}
