using Godswar.Server.Domain.World.Instances;

namespace Godswar.Server.Game;

internal sealed record BattlefieldTransporterDialogueContext(
    int AccountId,
    int CharacterId,
    string NpcKey,
    uint NpcInteractionId,
    byte SourceMapId,
    WorldInstanceId SourceWorldInstanceId,
    DateTimeOffset ExpiresAt,
    bool NiMiniPageIssued = false);
