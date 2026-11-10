using Godswar.Server.Domain.World.Instances;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    /// <summary>
    /// A 港湾遇袭 death revives at the run's own arrival. The generic path below
    /// it would restore the character's saved entry state, which is the capital,
    /// and would leave the run with one member fewer.
    /// </summary>
    private async Task<bool> TryHandleHarborAttackReviveAsync(
        CancellationToken cancellationToken)
    {
        if (_character is null ||
            !DynamicDungeonContentMapPolicy.IsHarborAttackMap(
                _character.CurrentMap) ||
            !_registry.TryGetHarborAttackInstance(_session, out var instanceId))
        {
            // Not a live run: the generic recovery path keeps its authority so a
            // character stranded on a harbor map can still get out.
            return false;
        }

        if (!_registry.TryReviveHarborAttackPlayer(
                _session,
                out instanceId,
                out var arrivalX,
                out var arrivalZ,
                out var lifeRevision))
        {
            // The run already resolved this request (deadline, termination, life
            // authority or vitals). The generic path must not run: its capital
            // restoration would tear the member out of the run.
            Console.WriteLine(
                "[harbor] revive refused character=" +
                $"{_character.Name} instance={instanceId}");
            return true;
        }

        try
        {
            var outcome = await TryBeginSameMapSceneTransitionAsync(
                arrivalX,
                arrivalZ,
                "harbor-revive",
                () => _registry.IsSessionInWorldInstance(_session, instanceId) &&
                    _registry.TryGetPlayerLifeRevision(
                        _session,
                        out var currentLife) &&
                    currentLife == lifeRevision,
                cancellationToken,
                publishRevivalVitals: true);
            Console.WriteLine(
                "[harbor] free revive character=" +
                $"{_character.Name} instance={instanceId} " +
                $"arrival={arrivalX:F2},{arrivalZ:F2} " +
                $"life={lifeRevision} outcome={outcome}");
            if (outcome == SceneTransitionOutcome.RejectedWithoutRelocation)
            {
                _session.Disconnect();
            }
        }
        catch
        {
            _session.Disconnect();
            throw;
        }

        return true;
    }
}
