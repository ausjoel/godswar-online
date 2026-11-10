using Godswar.Server.Application.Characters;
using Godswar.Server.Domain.World.Instances;

namespace Godswar.Server.Application.Progression;

internal enum FighterLevelSealChangeStatus
{
    CharacterNotFound,
    Sealed,
    Unsealed,
    AlreadySealed,
    AlreadyUnsealed,
    InsufficientBoundGold,
    RequestHashConflict
}

internal readonly record struct FighterLevelSealProjection(
    bool LevelSealed,
    int BindingGold,
    long SealRevision);

internal readonly record struct FighterLevelSealChangeResult(
    FighterLevelSealChangeStatus Status,
    bool LevelSealed,
    int BindingGold,
    long SealRevision,
    bool Replayed = false,
    FighterLevelSealProjection? ReplayProjection = null)
{
    public bool Changed => !Replayed && Status is
        FighterLevelSealChangeStatus.Sealed or
        FighterLevelSealChangeStatus.Unsealed;

    public bool RequiresLiveProjection =>
        Changed || Replayed;

    public FighterLevelSealProjection Projection =>
        ReplayProjection ?? (Replayed
            ? throw new InvalidOperationException(
                "A replay projection is required.")
            : new(LevelSealed, BindingGold, SealRevision));

    public bool RequiresWalletRefresh(int previousBindingGold) =>
        RequiresLiveProjection &&
        (Status == FighterLevelSealChangeStatus.Unsealed ||
         previousBindingGold != Projection.BindingGold);
}

internal interface IFighterLevelSealStore
{
    Task<FighterLevelSealChangeResult> ChangeFighterLevelSealAsync(
        int accountId,
        int characterId,
        RealmId realmId,
        PlayerOwnershipFence ownership,
        Guid operationId,
        bool desiredSealed,
        CancellationToken cancellationToken = default);
}

internal static class FighterLevelSealRules
{
    public const int UnsealBoundGoldCost = 10_000;
}
