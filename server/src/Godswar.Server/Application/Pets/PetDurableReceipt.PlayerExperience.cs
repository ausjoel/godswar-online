using Godswar.Server.Application.Commands;

namespace Godswar.Server.Application.Pets;

internal sealed partial record PetDurableReceipt
{
    private bool MatchesPlayerExperienceEvidence() =>
        Status == PetDurableReceiptStatus.PlayerExperienceAdded
            ? Family == CommandFamily.BagItemActivation && EquipmentSlot == -1 &&
                PetId == 0 && PetRevision == 0 &&
                PlayerExperience is { IsValid: true } evidence && evidence.KitBagSlot == KitBagSlot
            : PlayerExperience is null && (Status != PetDurableReceiptStatus.PlayerExperienceMaximumReached ||
                Family == CommandFamily.BagItemActivation && KitBagSlot >= 0 && EquipmentSlot == -1 && PetId == 0);
}
