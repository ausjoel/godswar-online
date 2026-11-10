using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Game;

namespace Godswar.Server.ProtocolChecks;

internal static partial class WonderlandPolicyChecks
{
    private static void CheckFinalGuardUnlock(IReadOnlyList<WonderlandSpawnPolicy> plan)
    {
        var bosses = plan.Where(monster => monster.IsBoss).Reverse().ToArray();
        var guard = plan.Single(monster => monster.Role == WonderlandMonsterRole.ChestGuard);
        Check.True(MonsterControlSkillPolicy.TryGet(354, out var silence), "native guard-control fixture exists");
        foreach (var mode in new[] { MonsterRuntimeMode.Legacy, MonsterRuntimeMode.Ecs })
        {
            var map = WonderlandMapChecks.Create(mode);
            var now = WonderlandMapChecks.EnterIsland(map, 8);
            var runtime = map.InitializeMonsters([], now);
            AssertGuardLocked("before any boss deaths");
            foreach (var boss in bosses[..^1])
            {
                WonderlandMapChecks.Kill(map, boss.ObjectId, now = now.AddMilliseconds(1));
                Check.True(map.TryGetWonderlandSnapshot(out var pending) && pending.State == WonderlandRunState.Active,
                    $"{mode}: Scorpion first and the next two bosses cannot trigger completion");
            }

            // Separate HP mutation from progression credit, as the real registry
            // does. A raw all-dead snapshot must not open the guard prematurely.
            Check.True(map.TryGetMonsterSnapshot(bosses[^1].ObjectId, out var lastBoss), "last boss is present");
            now = now.AddMilliseconds(1);
            Check.True(map.TryApplyMonsterDamageGuarded(lastBoss.ObjectId, lastBoss.CurrentHealth, 101,
                lastBoss.SpawnGeneration, lastBoss.HealthRevision, now, out var death) && death.Killed,
                $"{mode}: final boss HP reaches zero before its progression credit");
            var uncredited = map.AdvanceWonderland(now);
            Check.True(bosses.All(boss => map.TryGetMonsterSnapshot(boss.ObjectId, out var current) && !current.IsAlive) &&
                uncredited.State == WonderlandRunState.Active && uncredited.RequiredMonstersRemaining == 2 &&
                uncredited.CompletedIslands == 7 && uncredited.TerminalAt is null,
                $"{mode}: all four HP-zero bosses still leave the final uncredited boss and guard required");
            AssertGuardLocked("while the final boss death is uncredited");

            var credit = map.RecordCommittedWonderlandKill(map.WorldInstanceId, lastBoss.ObjectId,
                lastBoss.SpawnGeneration, now = now.AddMilliseconds(1));
            Check.True(credit.Outcome == WonderlandKillOutcome.Applied && credit.ClearedIsland is null &&
                credit.Snapshot.RequiredMonstersRemaining == 1 && credit.Snapshot.CompletedIslands == 7 &&
                credit.Snapshot.TerminalAt is null,
                $"{mode}: the fourth committed boss credit unlocks the guard without starting the countdown");
            Check.True(map.TryGetMonsterSnapshot(guard.ObjectId, out var unlocked) &&
                map.TryApplyMonsterPeriodicDamageGuarded(guard.ObjectId, 1, 101, unlocked.SpawnGeneration,
                    unlocked.HealthRevision, now, out var hit) && hit.AfterHealth == unlocked.CurrentHealth - 1 &&
                runtime.TryApplyControl(guard.ObjectId, 101, silence, unlocked.SpawnGeneration, now, out var control) &&
                    control.Applied,
                $"{mode}: the same valid periodic and control requests work after committed unlock");
            WonderlandMapChecks.Kill(map, guard.ObjectId, now = now.AddMilliseconds(1));
            Check.True(map.TryGetWonderlandSnapshot(out var complete) && complete.State == WonderlandRunState.Completed &&
                complete.CompletedIslands == 8 && plan.Where(monster => monster.Role == WonderlandMonsterRole.Atlas)
                    .All(monster => map.TryGetMonsterSnapshot(monster.ObjectId, out var survivor) && survivor.IsAlive),
                $"{mode}: killing the unlocked guard completes Wonderland while the optional Atlas remain alive");

            void AssertGuardLocked(string phase)
            {
                Check.True(map.TryGetMonsterSnapshot(guard.ObjectId, out var before), "locked guard is present");
                Check.True(!map.TryApplyMonsterDamageGuarded(guard.ObjectId, before.CurrentHealth, 101,
                        before.SpawnGeneration, before.HealthRevision, now, out _) &&
                    !map.TryApplyMonsterPeriodicDamageGuarded(guard.ObjectId, 1, 101,
                        before.SpawnGeneration, before.HealthRevision, now, out _) &&
                    !map.TryApplyMonsterStun(guard.ObjectId, 101, TimeSpan.FromSeconds(2),
                        before.SpawnGeneration, now, out _) &&
                    !runtime.TryApplyControl(guard.ObjectId, 101, silence, before.SpawnGeneration, now, out _) &&
                    map.TryGetMonsterSnapshot(guard.ObjectId, out var after) && before == after,
                    $"{mode}: direct damage, periodic damage, stun and control leave the guard unchanged {phase}");
            }
        }
    }
}
