using Godswar.Server.Application.Commands;

namespace Godswar.Server.Application.Pets;

internal sealed partial record PetDurableReceipt
{
    private bool MatchesProgressItemEvidence() => Status == PetDurableReceiptStatus.ProgressItemUsed
        ? Family == CommandFamily.BagItemActivation && EquipmentSlot == -1 && PetId == 0 && PetRevision == 0 &&
            ProgressItem is { IsValid: true } evidence && evidence.KitBagSlot == KitBagSlot
        : ProgressItem is null && (Status != PetDurableReceiptStatus.ProgressItemBalanceFull ||
            Family == CommandFamily.BagItemActivation && KitBagSlot is >= 0 and < 96 && EquipmentSlot == -1 && PetId == 0);
}
