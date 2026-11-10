using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed partial class MapInstance
{
    private MonsterRespawnPolicy? _monsterRespawnPolicy;

    public IMonsterMapRuntime InitializeMonsters(
        IReadOnlyList<CapturedMonsterSpawn> definitions,
        DateTimeOffset initializedAt,
        WorldBossRespawnState? activeWorldBossRespawn = null,
        MonsterRespawnPolicy respawnPolicy = MonsterRespawnPolicy.Timed)
    {
        MonsterRespawnPolicyRules.Validate(respawnPolicy);
        lock (_medusaOwnershipGate)
        {
            if (_medusaInstanceOwner is not null ||
                _medusaMonsterAttachment is not null)
            {
                throw new InvalidOperationException(
                    "Generic monster initialization is unavailable after " +
                    "Medusa ownership is bound.");
            }

            lock (_monsterRuntimeGate)
            {
                if (_monsterRuntime is not null)
                {
                    // Readiness may request the ordinary map seed after this
                    // instance has attached its owner-managed roster. Atlantis
                    // and the Wonderland islands both own their monsters and pin
                    // the respawn policy to Never, so the seed is already
                    // satisfied: handing the existing runtime back is what keeps
                    // the transition completion from reading the pinned policy
                    // as an illegal change and faulting the session.
                    if (ReferenceEquals(_monsterRuntime, _atlantisMonsters) ||
                        ReferenceEquals(_monsterRuntime, _wonderlandMonsters))
                    {
                        return _monsterRuntime;
                    }
                    if (_monsterRespawnPolicy != respawnPolicy)
                    {
                        throw new InvalidOperationException(
                            "A map's initialized monster respawn policy cannot be changed.");
                    }

                    return _monsterRuntime;
                }

                EnsureMonsterObjectIdsDoNotCollideWithNpcs(definitions);
                var runtime = MonsterMapRuntimeFactory.Create(
                    _monsterRuntimeMode,
                    MapId,
                    definitions,
                    initializedAt,
                    activeWorldBossRespawn: activeWorldBossRespawn,
                    worldBossCatalog: _worldBossCatalog,
                    respawnPolicy: respawnPolicy,
                    monsterCombatProfiles: _monsterCombatProfiles);
                _monsterRuntime = runtime;
                _monsterRespawnPolicy = respawnPolicy;
                return runtime;
            }
        }
    }
}
