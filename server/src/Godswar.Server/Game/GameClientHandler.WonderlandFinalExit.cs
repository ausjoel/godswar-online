using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Packets;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private async Task HandleWonderlandFinalExitAsync(WorldInstanceId instanceId, long lifeRevision,
        CancellationToken cancellationToken)
    {
        if (_registry.TryResolveWonderlandFinalExit(_session, instanceId, lifeRevision, out var command) &&
            await TryBeginAuthoritativeInstanceTransitionAsync(command, cancellationToken)) return;
        // Function57/sub107 is a native medal reward, not an eighth-island
        // lock message. Keep this authored exit explanation out of that route.
        if (_character?.CurrentMap == 207 && _character.CurrentHp > 0 &&
            _registry.IsSessionInWorldInstance(_session, instanceId) &&
            TryCaptureCurrentPlayerOwnership(out _))
            await _session.SendAsync(PacketBuilder.CenteredAnnouncement(
                    "Finish Wonderland and wait for its rewards before leaving through this teleporter."),
                cancellationToken, "WonderlandFinalExitLocked");
    }
}
