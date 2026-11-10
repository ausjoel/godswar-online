using Godswar.Server.Application.Characters;
using Godswar.Server.Application.Commands;

namespace Godswar.Server.Application.Pets;

internal enum PetCareDecayStatus : byte
{
    NoSummonedPet = 1,
    AlreadyExhausted = 2,
    Decayed = 3
}

internal sealed record PetCareDecayResult(
    PetCareDecayStatus Status,
    long PetId,
    int Satiety,
    int Amity,
    int RemainingLifetime,
    long PetRevision)
{
    public bool Changed => Status == PetCareDecayStatus.Decayed;
}

/// <summary>
/// Durable satiety and lifetime drain for the one summoned pet. The client
/// ships no decay rate of its own, so the project cadence is the only
/// authority; the drain is a normal ownership-fenced PostgreSQL mutation and
/// never runs while the pet is recalled or the player is offline.
/// </summary>
internal interface IPetCareDecayStore
{
    Task<PetCareDecayResult> DrainSummonedCareAsync(
        CommandSubject subject,
        PlayerOwnershipFence ownership,
        int satietyPoints,
        int lifetimePoints,
        CancellationToken cancellationToken = default);
}
