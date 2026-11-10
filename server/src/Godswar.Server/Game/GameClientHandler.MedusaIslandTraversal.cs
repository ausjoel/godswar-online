using Godswar.Server.Game.WorldInstances;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private async Task<bool> TryApplyMedusaIslandTraversalAsync(
        AcceptedMapMovementSegment movement,
        CancellationToken cancellationToken)
    {
        if (_character is null ||
            movement.MapId != _character.CurrentMap ||
            !MedusaIslandTraversalDetector.TryResolve(
                movement,
                MapPortalTriggerRadius,
                out var traversal) ||
            _registry.ResolveMedusaCharacterEffectAuthority(
                    _session,
                    DateTimeOffset.UtcNow).Outcome !=
                MedusaCharacterEffectAuthorityOutcome.ResolvedActive)
        {
            return false;
        }
        var sourceX = _character.PositionX;
        var sourceZ = _character.PositionZ;
        var transitioned = await TryBeginSameMapSceneTransitionAsync(
            traversal.TargetX,
            traversal.TargetZ,
            $"medusa-island:{traversal.SourceAnchorId}" +
            $"->{traversal.TargetAnchorId}",
            () => _registry.ResolveMedusaCharacterEffectAuthority(
                    _session,
                    DateTimeOffset.UtcNow).Outcome ==
                MedusaCharacterEffectAuthorityOutcome.ResolvedActive,
            cancellationToken);
        if (transitioned == SceneTransitionOutcome.RejectedWithoutRelocation)
        {
            return false;
        }
        if (transitioned == SceneTransitionOutcome.CommittedRequiresReconnect)
        {
            return true;
        }

        Console.WriteLine(
            "[instance] island transfer applied " +
            $"character={_character.Name} " +
            $"anchor={traversal.SourceAnchorId}" +
            $"->{traversal.TargetAnchorId} " +
            $"position={sourceX:F2},{sourceZ:F2}" +
            $"->{traversal.TargetX:F2},{traversal.TargetZ:F2}");
        return true;
    }
}
