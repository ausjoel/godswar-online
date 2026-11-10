using Godswar.Server.Game;
using Godswar.Server.Game.WorldInstances;

namespace Godswar.Server.ProtocolChecks;

internal static class MonsterOwnerCheckSteps
{
    internal static MonsterRuntimeTick Advance(
        WorldInstanceRuntime runtime, GameSessionRegistry registry, DateTimeOffset now)
    {
        var members = runtime.Owner.Invoke(static map => map.Snapshot(), TimeSpan.FromSeconds(3));
        var targets = MapInstance.CaptureMonsterCombatTargets(members,
            session => registry.TryGetPlayerLifeRevision(session, out var life) ? life : null);
        return runtime.Owner.Invoke(map => map.AdvanceMonsters(now, targets,
            session => registry.TryGetPlayerLifeRevision(session, out var life) ? life : null),
            TimeSpan.FromSeconds(3));
    }
}
