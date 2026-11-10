using Godswar.Server.Networking;
using Godswar.Server.World.Components.Players;

namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
    internal PlayerEcsSnapshot? GetPlayerMapEcsDiagnostics(
        ClientSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!_sessions.TryGetValue(session, out var context) ||
            !TryGetWorldInstance(context, out var runtime))
        {
            return null;
        }

        return InvokeWorldOwner(
            runtime,
            map => map.SnapshotEcsShadow()
                .Players
                .FirstOrDefault(player =>
                    player.Player.Identity.ObjectId == context.ObjectId)
                ?.Player);
    }
}
