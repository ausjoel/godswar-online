using Godswar.Server.Networking;
using Godswar.Server.Game.WorldInstances;

namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
    /// <summary>
    /// WL-SLIDERS rig aid (test servers only, GODSWAR_RIG_TEST_RUNS=1; the caller checks it): kills every enemy of the
    /// current Wonderland island through the normal damage and kill-scoring path, so a rig run can reach islands 6-8
    /// without a geared character. It never runs on live (the variable is not set there).
    /// </summary>
    internal string RigClearWonderlandIsland(ClientSession session, DateTimeOffset now)
    {
        if (!_sessions.TryGetValue(session, out var context) || !TryGetWorldInstance(context, out var runtime) ||
            runtime.MapId != WonderlandMapId)
            return "not in a Wonderland run";
        var island = InvokeWorldOwner(runtime, map => map.TryGetWonderlandSnapshot(out var run) ? run.CurrentIsland : 0);
        var killed = 0;
        foreach (var monster in runtime.Map.SnapshotMonsters().Where(m => m.IsAlive && m.IsSpawned))
        {
            if (!runtime.Map.TryGetWonderlandSpawnPolicy(monster.ObjectId, out var policy) ||
                policy.Stage != island || policy.IsAllied) continue;
            var result = InvokeWorldOwnerAuthoritativeMutation(runtime, map =>
            {
                MonsterDamageResult committed = null!;
                var applied = map.TryApplyMonsterDamage(monster.ObjectId, monster.CurrentHealth, context.CharacterId,
                    monster.SpawnGeneration, now, out committed);
                return (Applied: applied, Damage: committed);
            });
            if (!result.Applied) continue;
            ObserveWonderlandMonsterDamageCommitted(runtime, result.Damage, now);
            RecordWonderlandMonsterKillCommitted(runtime, result.Damage, now);
            if (result.Damage.Killed) killed++;
        }
        return $"wlclear island={island} killed={killed}";
    }
}
