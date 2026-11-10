using Godswar.Server.Application.Characters;
using Godswar.Server.Application.Commands;
using Godswar.Server.Domain.World.Instances;

namespace Godswar.Server.Application.Progression;

internal enum DeveloperProgressionOperation : byte
{
    SetFighterLevel = 1,
    AdjustFighterLevel = 2,
    AddTalentPoints = 3,
    AddZodiacEnergy = 4
}

internal readonly record struct DeveloperProgressionCommand(
    DeveloperProgressionOperation Operation,
    int Value)
{
    public bool IsValid => Operation switch
    {
        DeveloperProgressionOperation.SetFighterLevel =>
            Value is >= 1 and <= 200,
        DeveloperProgressionOperation.AdjustFighterLevel =>
            Value is >= -199 and <= 199 and not 0,
        DeveloperProgressionOperation.AddTalentPoints or
            DeveloperProgressionOperation.AddZodiacEnergy =>
            Value > 0,
        _ => false
    };
}

internal sealed record DeveloperProgressionCommandRequest(
    CommandSubject Subject,
    RealmId RealmId,
    PlayerOwnershipFence Ownership,
    DeveloperProgressionCommand Command)
{
    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(
            Subject.AccountId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(
            Subject.CharacterId);
        if (!RealmId.IsValid)
        {
            throw new ArgumentOutOfRangeException(nameof(RealmId));
        }

        Ownership.Validate();
        if (!Command.IsValid)
        {
            throw new ArgumentOutOfRangeException(nameof(Command));
        }
    }
}

internal readonly record struct DeveloperProgressionProjection(
    int FighterLevel,
    long FighterExperience,
    int TalentPoints,
    byte ZodiacLevel,
    int ZodiacEnergy,
    int ZodiacEnergyRemainderX100,
    long ProgressionRevision);

internal enum DeveloperProgressionMutationStatus : byte
{
    Committed = 1,
    Unchanged = 2,
    CharacterNotFound = 3,
    TargetOutOfRange = 4,
    ArithmeticOverflow = 5,
    ZodiacStorageLimitExceeded = 6,
    RevisionExhausted = 7,
    ProviderUnavailable = 8
}

internal sealed record DeveloperProgressionMutationResult(
    DeveloperProgressionMutationStatus Status,
    DeveloperProgressionProjection? Previous,
    DeveloperProgressionProjection? Current)
{
    public bool IsSuccess => Status is
        DeveloperProgressionMutationStatus.Committed or
        DeveloperProgressionMutationStatus.Unchanged;

    public bool Changed =>
        Status == DeveloperProgressionMutationStatus.Committed;

    public static DeveloperProgressionMutationResult
        ProviderUnavailable() =>
        new(
            DeveloperProgressionMutationStatus.ProviderUnavailable,
            Previous: null,
            Current: null);
}

/// <summary>
/// Executes one allowlisted developer progression mutation against the
/// currently owned character. Implementations must lock and validate the
/// ownership fence for the complete durable mutation and revalidate it after
/// commit.
/// </summary>
internal interface IDeveloperProgressionCommandExecutor
{
    Task<DeveloperProgressionMutationResult> ExecuteAsync(
        DeveloperProgressionCommandRequest request,
        CancellationToken cancellationToken = default);
}
