using Godswar.Server.Game;

namespace Godswar.Server.ProtocolChecks;

internal static partial class WonderlandCombatChecks
{
    private static async Task CheckCapturedCommitAcrossTickAsync(MonsterRuntimeMode monsters, PlayerRuntimeMode players)
    {
        await using (var f = await Fixture.CreateAsync(1, monsters, players))
        {
            var capturedAt = f.Now;
            var optional = f.Monster("tower");
            var worldTick = f.Now.AddSeconds(.5);
            await f.Registry.AdvanceWonderlandCombatAsync(f.Runtime, worldTick, CancellationToken.None);
            f.DirectHit("alpha", f.Monster("alpha").CurrentHealth);
            f.Runtime.Map.TryGetWonderlandSnapshot(out var run);
            Check.True(f.Now == capturedAt && run.CurrentIsland == 2 && run.LastIslandClearedAt == worldTick,
                "an attack captured before a concurrent world tick commits progress at the monotonic owner time");
            var hp = optional.CurrentHealth;
            Check.True(f.TryDirectHit(optional, 100, out _) &&
                f.Runtime.Map.TryGetMonsterSnapshot(optional.ObjectId, out var current) && current.CurrentHealth == hp - 100 &&
                current.IsAlive && current.IsSpawned,
                "surviving earlier-island actors remain damageable at the monotonic owner time");
        }
        await using (var f = await Fixture.CreateAsync(4, monsters, players))
        {
            var hp = f.Character.CurrentHp;
            await f.Registry.AdvanceWonderlandCombatAsync(f.Runtime, f.Now.AddSeconds(.5), CancellationToken.None);
            f.DirectHit("rock", 400);
            Check.Equal(hp - 40, f.Character.CurrentHp,
                "captured direct damage still reflects exactly once after an intervening world tick");
            f.Runtime.Map.CancelWonderland(f.Now.AddSeconds(.75));
            var monsterHp = f.Monster("rock").CurrentHealth;
            Check.True(!f.TryDirectHit("rock", 400, out _) && f.Monster("rock").CurrentHealth == monsterHp,
                "cancellation rejects a stale captured hit before mutating monster HP");
        }
    }
}
