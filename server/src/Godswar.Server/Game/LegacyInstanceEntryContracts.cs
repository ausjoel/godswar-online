using Godswar.Server.Application.Characters;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Networking;

namespace Godswar.Server.Game;

internal enum LegacyInstanceEntryStatus : byte
{
    Ready = 1,
    LeaderRequired = 2,
    PartyUnavailable = 3,
    PartyTooSmall = 4,
    PartyTooLarge = 5,
    LevelRequirementNotMet = 6,
    RuntimeUnavailable = 7,
    TransferFailed = 8,
    ScheduleClosed = 9
}

internal sealed record LegacyInstancePartySnapshot(
    long? PartyId,
    RealmId RealmId,
    int LeaderCharacterId,
    IReadOnlyList<LegacyInstancePartyMember> Members);

/// <summary>
/// One member of the party an exact instance was admitted for.
/// </summary>
/// <remarks>
/// The instance's roster panel publishes this record for a member whose session
/// is gone, so it carries the presentation values that outlive the session -
/// including <paramref name="Profession"/>, which the old live-session-only
/// roster read from the character itself.
/// </remarks>
internal sealed record LegacyInstancePartyMember(
    ClientSession Session,
    int AccountId,
    int CharacterId,
    string CharacterName,
    int Level,
    byte Profession,
    RealmId RealmId,
    WorldInstanceId SourceWorldInstanceId,
    byte SourceMapId,
    PlayerOwnershipFence Ownership);
