using Godswar.Server.Domain.World.Content;
using Godswar.Server.Domain.World.Instances;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    // Both native repetition controls share the existing packet-shape checks.
    // The current server-owned instance, never the supplied repetition ID,
    // decides which dungeon receives the action.
    private bool TryHandleHarborAttackTermination(
        int? repetitionId,
        int repetitionIndex)
    {
        if (_character is null ||
            !DynamicDungeonContentMapPolicy.IsHarborAttackMap(
                _character.CurrentMap))
        {
            return false;
        }

        var sceneId = HarborAttackClientSceneId(_character.CurrentMap);
        // The panel's Terminate control arrives with no repetition id; the
        // twelve-byte end request carries the scene it belongs to.
        if (repetitionId is { } requested && requested != sceneId)
        {
            return false;
        }

        // The run is terminal from here. The world tick publishes the native
        // leave countdown to every member inside, exactly as a completed run
        // does - the shared end-of-run flow - so no reset is sent: it would wipe
        // the panel the countdown needs.
        return _registry.TryTerminateHarborAttackRunFromLeader(
            _session,
            sceneId,
            repetitionIndex);
    }

    private static int HarborAttackClientSceneId(byte mapId) =>
        mapId == DynamicDungeonContentMapPolicy.HarborAttackSecondMapId
            ? InstanceCallerProtocol.HarborAttackSecondClientSceneId
            : InstanceCallerProtocol.HarborAttackFirstClientSceneId;
}
