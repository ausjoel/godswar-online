using Godswar.Server.Application.Characters;
using Godswar.Server.Application.Commands;
using Godswar.Server.Domain.World.Instances;

namespace Godswar.Server.Application.WorldInstances;

internal sealed record WonderlandChestClaimRequest(WorldInstanceId WorldInstanceId, RealmId RealmId,
    int Island, byte PartyCamp, string MilestoneHash, CommandSubject Subject, PlayerOwnershipFence Ownership)
{
    public bool IsValid => WorldInstanceId.IsValid && RealmId.IsValid && Island is >= 1 and <= 8 &&
        PartyCamp is 0 or 1 && MilestoneHash is { Length: 64 } &&
        MilestoneHash.All(character => character is >= '0' and <= '9' or >= 'A' and <= 'F') &&
        Subject.AccountId > 0 && Subject.CharacterId > 0 && Ownership.IsValid;
}

internal readonly record struct WonderlandChestItemReward(uint ItemId, short Quantity, short Bound);

internal enum WonderlandChestClaimStatus
{
    Claimed, AlreadyClaimed, NotEligible, OwnershipLost, InventoryFull, Unavailable
}

internal sealed record WonderlandChestClaimReceipt(WonderlandChestClaimStatus Status,
    long InventoryRevision, IReadOnlyList<WonderlandChestItemReward> Rewards)
{
    public bool Succeeded => Status is WonderlandChestClaimStatus.Claimed or WonderlandChestClaimStatus.AlreadyClaimed;
}

internal interface IWonderlandChestClaimStore
{
    Task<WonderlandChestClaimReceipt> ClaimAsync(WonderlandChestClaimRequest request,
        CancellationToken cancellationToken = default);

    Task<WonderlandChestClaimReceipt> ClaimBossAsync(WonderlandBossLootClaimRequest request,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new WonderlandChestClaimReceipt(WonderlandChestClaimStatus.Unavailable, 0, []));

}
