using Godswar.Server.Application.Commands;

namespace Godswar.Server.Application.Pets;

internal sealed partial record PetDurableReceipt
{
    private bool MatchesWonderlandSackEvidence() =>
        Status == PetDurableReceiptStatus.WonderlandSackOpened
            ? Family == CommandFamily.BagItemActivation && KitBagSlot >= 0 &&
                EquipmentSlot == -1 && PetId == 0 && PetRevision == 0 &&
                WonderlandSack is { IsValid: true } evidence && evidence.KitBagSlot == KitBagSlot
            : WonderlandSack is null && (Status != PetDurableReceiptStatus.WonderlandSackBagFull ||
                Family == CommandFamily.BagItemActivation && KitBagSlot >= 0 && EquipmentSlot == -1 && PetId == 0);
}
