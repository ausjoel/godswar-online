using Godswar.Server.Domain.World.Instances;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private byte? _unavailableInstanceReconnectMap;

    /// <summary>
    /// The exact instance this login is going back into, resolved while the
    /// character was loaded and joined on the client's own EnterGame.
    /// </summary>
    private GameSessionRegistry.InstanceReconnectTarget?
        _pendingInstanceReconnect;

    private void PrepareUnavailableInstanceReconnectRecovery()
    {
        if (_character is null ||
            _session.GatewayWorldAdmission is not null)
        {
            return;
        }

        var savedMap = _character.CurrentMap;
        ReviveTrace.Log($"PREPARE-RECONNECT savedMap={savedMap}");
        if (TryRecoverPansLabyrinthLogin() || TryRecoverEventDestinationLogin())
        {
            return;
        }

        if (TryRecoverHeraLabyrinthLogin()) // HERA'S LABYRINTH
        {
            return;
        }

        if (TryRecoverRomanceIslandLogin()) // PROF slice 2
        {
            return;
        }

        if (!DynamicDungeonContentMapPolicy.IsDynamicDungeonMap(savedMap))
        {
            ReviveTrace.Log("PREPARE-RECONNECT no-recovery");
            return;
        }

        // A run this character is still admitted to, that is still running and
        // that still holds somebody else, takes him back to its own entrance.
        // Everything else keeps the old behaviour: the capital.
        if (_registry.TryResolveInstanceReconnect(
                _character.Id,
                _character.AccountId,
                _character.RealmId,
                savedMap,
                out var reconnect))
        {
            _character.PositionX = reconnect.EntranceX;
            _character.PositionZ = reconnect.EntranceZ;
            _pendingInstanceReconnect = reconnect;
            _unavailableInstanceReconnectMap = savedMap;
            _positionDirty = true;
            ReviveTrace.Log(
                $"PREPARE-RECONNECT instance map={savedMap} " +
                $"instance={reconnect.InstanceId} " +
                $"pos={reconnect.EntranceX:F2},{reconnect.EntranceZ:F2}");
            return;
        }

        if (!GameDefaults.TryRecoverUnavailableInstanceLocation(
                _character))
        {
            ReviveTrace.Log("PREPARE-RECONNECT no-recovery");
            return;
        }

        ReviveTrace.Log($"PREPARE-RECONNECT recovered map={_character.CurrentMap} pos={_character.PositionX:F2},{_character.PositionZ:F2}");
        _unavailableInstanceReconnectMap = savedMap;
        _positionDirty = true;
    }

    /// <summary>
    /// Enters the instance the login resolved, before the client's own world
    /// handshake, exactly as an admitted member's entry does.
    /// </summary>
    /// <remarks>
    /// The join must happen before the world bootstrap publishes the map's NPCs
    /// and monsters, because that publication resolves the instance from this
    /// session's own membership. A failure - the run ended between the login and
    /// the entry, or the character is no longer admitted - falls back to the same
    /// capital recovery an unavailable instance has always used.
    /// </remarks>
    private bool TryEnterReconnectedInstance()
    {
        if (_pendingInstanceReconnect is not { } reconnect ||
            _character is null ||
            _account is null)
        {
            return true;
        }

        _pendingInstanceReconnect = null;
        try
        {
            var objectId = _registry.JoinPlayerReconnectedInstance(
                _session,
                _account.Id,
                _character,
                reconnect.InstanceId,
                worldReady: false);
            _registered = true;
            Console.WriteLine(
                "[instance] reconnect re-entered running instance " +
                $"character={_character.Name} map={reconnect.MapId} " +
                $"instance={reconnect.InstanceId} run={reconnect.Kind} " +
                $"object={objectId} " +
                $"pos={reconnect.EntranceX:F2},{reconnect.EntranceZ:F2}");
            // A Medusa member's progress panel is a per-session registration, so
            // the run's own panel publisher has to be opened again for the new
            // session. The other three publish from the run on every tick.
            _registry.RestoreMedusaInstancePanelAfterReconnect(_session);
            return true;
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Console.WriteLine(
                "[instance] reconnect fallback to capital " +
                $"character={_character.Name} map={reconnect.MapId} " +
                $"instance={reconnect.InstanceId}: {error.Message}");
            GameDefaults.InitializeStartingLocation(_character);
            _unavailableInstanceReconnectMap = reconnect.MapId;
            _positionDirty = true;
            ReviveTrace.Log(
                $"RECONNECT-FALLBACK map={_character.CurrentMap} " +
                $"pos={_character.PositionX:F2},{_character.PositionZ:F2}");
            return false;
        }
    }

    private async Task<bool>
        PersistUnavailableInstanceReconnectRecoveryAsync(
            CancellationToken cancellationToken)
    {
        if (!_unavailableInstanceReconnectMap.HasValue)
        {
            return true;
        }
        if (_character is null)
        {
            _session.Disconnect();
            return false;
        }

        _character.MarkPositionChanged();
        if (!await PersistPositionCheckpointAsync(
                _character,
                force: true,
                cancellationToken))
        {
            _session.Disconnect();
            return false;
        }

        var recoveredMap = _unavailableInstanceReconnectMap.Value;
        _unavailableInstanceReconnectMap = null;
        _positionDirty = false;
        _lastPositionPersistUtc = DateTime.UtcNow;
        Console.WriteLine(
            _character.CurrentMap == recoveredMap
                ? "[instance] reconnect checkpoint restored " +
                    $"character={_character.Name} map={recoveredMap} " +
                    $"pos={_character.PositionX:F2},{_character.PositionZ:F2}"
                : "[instance] recovered unavailable saved instance " +
                    $"character={_character.Name} map={recoveredMap}->" +
                    $"{_character.CurrentMap}");
        return true;
    }
}
