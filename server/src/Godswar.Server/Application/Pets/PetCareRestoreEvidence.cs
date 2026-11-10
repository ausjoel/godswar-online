namespace Godswar.Server.Application.Pets;

/// <summary>
/// Durable evidence for a reviewed pet-care consumable that was fed to the
/// summoned pet. Every number is the post-commit authoritative row: the
/// projection that follows must never re-derive them in memory.
/// </summary>
internal sealed record PetCareRestoreEvidence(
    long PetId,
    int Satiety,
    int Amity,
    int RemainingLifetime,
    int CurrentEnergy,
    int MaximumEnergy,
    long PetRevision,
    uint ItemId)
{
    public bool IsValid =>
        PetId > 0 &&
        Satiety >= 0 &&
        Amity >= 0 &&
        RemainingLifetime >= 0 &&
        MaximumEnergy > 0 &&
        CurrentEnergy >= 0 &&
        CurrentEnergy <= MaximumEnergy &&
        PetRevision > 0 &&
        ItemId > 0;
}
