using Godswar.Server.Application.Characters;
using Godswar.Server.Domain.World.Instances;

namespace Godswar.Server.Game;

/// <summary>
/// Requests a fenced transfer between two exact world-instance identities.
/// The destination may be a dynamic dungeon; it is never resolved by map ID.
/// </summary>
internal readonly record struct AuthoritativeInstanceTransitionCommand(
    int CharacterId,
    WorldInstanceId ExpectedSourceWorldInstanceId,
    byte ExpectedSourceMapId,
    PlayerOwnershipFence ExpectedOwnership,
    WorldInstanceId TargetWorldInstanceId,
    byte TargetMapId,
    float TargetX,
    float TargetZ)
{
    // Voluntary exits can bind the living player who clicked. Forced instance
    // egress leaves this null so a dead player can still be returned safely.
    public long? RequiredLivingLifeRevision { get; init; }

    public static AuthoritativeInstanceTransitionCommand FromMedusa(
        in MedusaInstanceTransitionCommand command) => new(
        command.CharacterId,
        command.ExpectedSourceWorldInstanceId,
        command.ExpectedSourceMapId,
        command.ExpectedOwnership,
        command.TargetWorldInstanceId,
        command.TargetMapId,
        command.TargetX,
        command.TargetZ);
}
