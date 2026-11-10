using Godswar.Server.Application.Commands;

namespace Godswar.Server.Application.Characters;

// CLASSCHANGE: the store contract lives in Application so the Game handler does not reference
// Infrastructure (data-boundary ratchet); PostgresCharacterClassChangeStore implements it.
internal enum CharacterClassChangeStatus
{
    Done,
    CharacterNotFound,
    Stale,
    SameClass,
    EquipmentWorn,
    PermitMissing
}

/// <summary>What the change left behind, for the live character, the log and the reply.</summary>
internal readonly record struct CharacterClassChangeResult(
    CharacterClassChangeStatus Status,
    byte Profession = 0,
    int TalentPoints = 0,
    int TalentPointsRefunded = 0,
    int SealedSkills = 0,
    int UnsealedSkills = 0,
    int GrantedSkills = 0,
    bool PermitUsed = false,
    long ReceiptId = 0);

/// <summary>
/// CLASSCHANGE: the Class Shifter. One ownership-fenced transaction re-checks every rule on
/// the locked row, uses the permit (with the inventory ledger row every durable consumer
/// writes), converts the old class's talents into talent points, seals the old class's
/// skills, unseals and grants the new class's, clears attack zodiac selections the new
/// class cannot use, writes the new profession and a receipt. Nothing is committed unless
/// every step succeeds.
/// </summary>
internal interface ICharacterClassChangeStore
{
    Task<CharacterClassChangeResult> ChangeClassAsync(
        CommandSubject subject,
        PlayerOwnershipFence ownership,
        byte expectedProfession,
        byte targetProfession,
        int freeLevel,
        CancellationToken cancellationToken);
}

internal static class CharacterClassChangeControls
{
    public static ICharacterClassChangeStore? Store { get; set; }
}
