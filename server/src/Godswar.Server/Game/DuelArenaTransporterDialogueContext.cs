using Godswar.Server.Domain.World.Instances;

namespace Godswar.Server.Game;

internal sealed record DuelArenaTransporterDialogueContext(
    int AccountId,
    int CharacterId,
    string NpcKey,
    uint NpcInteractionId,
    byte SourceMapId,
    WorldInstanceId SourceWorldInstanceId,
    DateTimeOffset ExpiresAt);
