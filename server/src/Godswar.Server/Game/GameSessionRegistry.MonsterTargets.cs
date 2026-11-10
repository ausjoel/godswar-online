using Godswar.Server.Game.WorldInstances;
using Godswar.Server.Networking;

namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
    private MonsterRuntimeTick AdvanceMonsterWorldRuntime(
        WorldInstanceRuntime runtime, DateTimeOffset now)
    {
        var members = InvokeWorldOwner(runtime, static map => map.Snapshot());
        Func<ClientSession, long?>? lifeResolver = _playerRuntimeMode == PlayerRuntimeMode.Ecs
            ? session => TryGetPlayerLifeRevision(session, out var life) ? life : null
            : null;
        // Never retain the map owner while waiting on a character's vitals.
        // Elemental secondary effects hold vitals while submitting to this owner.
        var targets = MapInstance.CaptureMonsterCombatTargets(
            members, lifeResolver, MonsterCombatTargetProjectionStartingHook);
        return InvokeWorldOwner(runtime, map => map.AdvanceMonsters(now, targets, lifeResolver));
    }
}
