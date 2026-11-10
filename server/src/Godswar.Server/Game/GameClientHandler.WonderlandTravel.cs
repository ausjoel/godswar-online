using Godswar.Server.Domain.World.Content;
using Godswar.Server.Protocol;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private async Task<bool> TryApplyWonderlandIslandTraversalAsync(AcceptedMapMovementSegment movement,
        CancellationToken cancellationToken)
    {
        if (_character?.CurrentMap != 207 || !WonderlandTraversalPolicy.TryDetect(movement, out var island))
            return false;
        return await TravelWonderlandIslandAsync(island, cancellationToken);
    }

    private async Task<bool> TravelWonderlandIslandAsync(int island, CancellationToken cancellationToken)
    {
        if (!_registry.TryResolveWonderlandTravel(_session, island, out var instanceId, out var arrival)) return false;
        var outcome = await TryBeginSameMapSceneTransitionAsync(arrival.X, arrival.Z,
            $"wonderland-island:{island}->{island + 1}",
            () => _registry.IsWonderlandTravelCurrent(_session, instanceId, island), cancellationToken);
        if (outcome == SceneTransitionOutcome.RejectedWithoutRelocation) return false;
        _registry.ClearWonderlandPlayerEffects(_session);
        return true;
    }

    private bool TryHandleWonderlandTermination(int? repetitionId,
        int repetitionIndex)
    {
        if (_character?.CurrentMap != 207) return false;
        // The run is terminal from here. The world tick publishes the native
        // leave countdown to every member inside, exactly as completion does -
        // the shared end-of-run flow - so no reset is sent here: that would wipe
        // the panel the countdown needs.
        var terminated = _registry.TryTerminateWonderlandRun(_session, repetitionId,
            repetitionIndex, DateTimeOffset.UtcNow);
        if (terminated)
            Console.WriteLine($"[wonderland] termination accepted character={_character.Name} " +
                $"hp={_character.CurrentHp} repetition={repetitionId?.ToString() ?? "panel"} index={repetitionIndex}");
        return terminated;
    }

    private async Task<bool> TryHandleWonderlandReviveAsync(CancellationToken cancellationToken)
    {
        if (_character?.CurrentMap != 207) return false;
        if (!_registry.TryReviveWonderlandPlayer(_session, out var instanceId, out var arrival, out var lifeRevision))
            return true;
        try
        {
            _registry.ClearWonderlandPlayerEffects(_session);
            var outcome = await TryBeginSameMapSceneTransitionAsync(arrival.X, arrival.Z, "wonderland-revive",
                () => _registry.IsSessionInWorldInstance(_session, instanceId) &&
                    _registry.TryGetPlayerLifeRevision(_session, out var currentLife) && currentLife == lifeRevision,
                cancellationToken, publishRevivalVitals: true);
            Console.WriteLine($"[wonderland] free revive character={_character.Name} instance={instanceId} " +
                $"arrival={arrival.X:F2},{arrival.Z:F2} life={lifeRevision} outcome={outcome}");
            if (outcome == SceneTransitionOutcome.RejectedWithoutRelocation) _session.Disconnect();
        }
        catch
        {
            _session.Disconnect();
            throw;
        }
        return true;
    }
}
