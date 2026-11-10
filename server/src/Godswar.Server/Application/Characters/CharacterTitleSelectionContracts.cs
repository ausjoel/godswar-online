using Godswar.Server.Application.Commands;
using Godswar.Server.Domain.World.Instances;

namespace Godswar.Server.Application.Characters;

internal readonly record struct CharacterTitleSelectionRequest(
    CommandSubject Subject,
    RealmId RealmId,
    PlayerOwnershipFence Ownership,
    uint TitleId);

internal enum CharacterTitleSelectionStatus : byte
{
    Applied = 1,
    Unchanged = 2,
    CharacterUnavailable = 3,
    OwnershipLost = 4,
    TitleNotOwned = 5,
    RevisionExhausted = 6
}

/// <summary>
/// Carries the wallet and selection at one shared reward revision. Callers must
/// revalidate the session fence before projecting it and ignore older revisions.
/// </summary>
internal sealed record CharacterTitleSelectionReceipt(
    CharacterTitleSelectionStatus Status,
    uint SelectedTitleId,
    int HonorPoints,
    long RewardRevision,
    IReadOnlyList<uint> OwnedTitleIds)
{
    public bool Succeeded => Status is CharacterTitleSelectionStatus.Applied or
        CharacterTitleSelectionStatus.Unchanged;
}

internal interface ICharacterTitleSelectionStore
{
    Task<CharacterTitleSelectionReceipt> SelectAsync(
        CharacterTitleSelectionRequest request,
        CancellationToken cancellationToken = default);
}
