using Godswar.Server.Domain.World.Instances;

namespace Godswar.Server.Game;

/// <summary>
/// Short-lived proof that an authenticated living character opened an exact
/// ordinary Transporter menu while near that NPC in the same world instance.
/// </summary>
internal sealed record TransporterDialogueContext(
    int AccountId,
    int CharacterId,
    string NpcKey,
    uint NpcInteractionId,
    short SourceMapId,
    WorldInstanceId SourceWorldInstanceId,
    DateTimeOffset ExpiresAt);
