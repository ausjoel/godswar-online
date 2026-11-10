using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Packets;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    // Both native repetition controls share the existing packet-shape checks.
    // The current server-owned instance, never the supplied repetition ID,
    // decides which dungeon receives the action. Atlantis contributes only the
    // rule for ending its own active run; the terminal flow itself - the four
    // frames, the thirty-second countdown, the leaving member and the list clear
    // - is the shared InstanceRunEnding implementation.
    private bool TryHandleAtlantisTermination(int? repetitionId,
        int repetitionIndex)
    {
        if (_character?.CurrentMap != DynamicDungeonContentMapPolicy.AtlantisPortalMapId)
        {
            return false;
        }
        // The run is terminal from here. The world tick publishes the native leave
        // countdown to every member inside, exactly as 飘渺幻境, 港湾遇袭 and
        // 美杜莎之岛 do, so no reset is sent: a zero-second 10231 here wiped the
        // panel the leader had just been given and left him unable to leave.
        return repetitionId is { } id
            ? _registry.TryEndAtlantisRunFromLeader(_session, id, repetitionIndex, DateTimeOffset.UtcNow)
            : _registry.TryTerminateAtlantisRunFromLeader(_session, DateTimeOffset.UtcNow);
    }
}
