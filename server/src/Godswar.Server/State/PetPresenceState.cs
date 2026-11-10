namespace Godswar.Server.State;

internal enum PetPresenceOperation
{
    Take,
    CallOut,
    Recall,

    /// <summary>
    /// Permanently destroys an owned pet (native opcode 10238).
    /// </summary>
    /// <remarks>
    /// The reference server answers a successful discard with pet-operation
    /// result code 3 and no pet record, so this is a physical delete rather
    /// than a state change: the row leaves <c>character_pets</c> and the
    /// cascading child tables with it. A pet that is still carried is refused
    /// instead, because the carried pet is the one the native client draws and
    /// resolves skill passives from; the player recalls it first.
    /// </remarks>
    Delete
}

internal enum PetPresenceTransitionStatus
{
    Succeeded,
    CharacterNotFound,
    PetNotFound,
    PetUnavailable,
    PetNotTaken
}

internal sealed record PetPresenceTransitionResult(
    PetPresenceTransitionStatus Status,
    long PetId,
    bool IsCarried,
    bool IsSummoned)
{
    public bool Succeeded =>
        Status == PetPresenceTransitionStatus.Succeeded;
}
