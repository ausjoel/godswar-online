using Godswar.Server.Application.Pets;
using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Packets;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private bool IsWonderlandSackProjection(PetDurableReceipt receipt) =>
        receipt.Status is PetDurableReceiptStatus.WonderlandSackOpened or PetDurableReceiptStatus.WonderlandSackBagFull ||
        receipt.Status == PetDurableReceiptStatus.ConsumableCooldownActive &&
            _character is not null && receipt.KitBagSlot >= 0 &&
            WonderlandSackRewardPolicy.IsSack(KitBagSlots.GetItemId(_character.KitBag, receipt.KitBagSlot));

    private async Task<bool> SendWonderlandSackProjectionAsync(PetDurableReceipt receipt,
        PetDurableExecutionDisposition disposition, string bagBefore, CancellationToken token)
    {
        if (_character is null) return false;
        IReadOnlyList<WonderlandChestItemReward> rewards = [];
        if (receipt.Succeeded && disposition == PetDurableExecutionDisposition.Committed)
        {
            var evidence = receipt.WonderlandSack ??
                throw new InvalidDataException("A committed Wonderland sack requires its durable reward evidence.");
            rewards = [new(evidence.RewardItemId, evidence.RewardQuantity, evidence.RewardBound)];
        }
        await SendWonderlandRewardProjectionAsync(bagBefore, rewards, token);
        return true;
    }
}
