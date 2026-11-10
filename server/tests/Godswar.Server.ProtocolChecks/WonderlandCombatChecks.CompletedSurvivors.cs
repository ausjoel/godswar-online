using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Game;
using Godswar.Server.State;
using Godswar.Server.World.Systems.Combat;

namespace Godswar.Server.ProtocolChecks;

internal static partial class WonderlandCombatChecks
{
    public const string CompletedSurvivorCheckName = "Wonderland completed treasure window preserves guarded survivor combat";

    public static async Task RunCompletedSurvivorsAsync()
    {
        foreach (var monsters in new[] { MonsterRuntimeMode.Legacy, MonsterRuntimeMode.Ecs })
        foreach (var players in new[] { PlayerRuntimeMode.Legacy, PlayerRuntimeMode.Ecs })
        {
            await using var f = await Fixture.CreateAsync(8, monsters, players);
            f.Runtime.Map.TryGetWonderlandSnapshot(out var active);
            foreach (var required in active.ActiveSpawns.Where(actor => actor.RequiredForProgression))
                WonderlandMapChecks.Kill(f.Runtime.Map, required.ObjectId, f.Now = f.Now.AddMilliseconds(1));
            f.Runtime.Map.TryGetWonderlandSnapshot(out var complete);
            Check.True(complete.State == WonderlandRunState.Completed, "fixture earns the four final bosses and chest guard");
            var survivor = f.Monster("atlas");
            var beforeHp = f.Character.CurrentHp;
            await f.IncomingAsync(survivor);
            Check.True(f.Character.CurrentHp < beforeHp,
                "the living native survivor's attack commits real player damage after completion");
            Check.True(f.TryDirectHit(survivor, 1, out var hit) && hit.AfterHealth == survivor.CurrentHealth - 1,
                "the original participant can damage the optional survivor through the real registry transaction");
            // The convenience hit fixture intentionally recaptures current HP.
            // Submit the old revision explicitly to exercise the actual fence.
            Check.True(f.Registry.TryCapturePlayerMonsterTarget(f.Session, 207, survivor.ObjectId,
                out _, out var currentAuthority), "capture current player authority without refreshing the old monster revision");
            var staleResolution = AuthoredCombatPveCurrent.ResolveBasicAttack(
                new CombatAttackerStats { Level = 130, PhysicalAttack = 10_000, Hit = 10_000 },
                new CombatTargetStats { Level = 130 }, 100_000) with { Damage = 1, Outcome = CombatHitOutcome.Normal };
            Check.True(!f.Registry.TryCommitPlayerMonsterDamageGuarded(f.Session, 207, survivor.ObjectId,
                    survivor.RuntimeInstanceId, f.Character.Id, survivor.SpawnGeneration, survivor.HealthRevision,
                    currentAuthority, f.Now, staleResolution, out _) &&
                f.Monster("atlas").CurrentHealth == hit.AfterHealth,
                "completed combat does not admit an old monster health revision");
            var current = f.Monster("atlas");
            MonsterControlSkillPolicy.TryGet(74, out var stun);
            Check.True(f.Registry.TryCapturePlayerMonsterTarget(f.Session, 207, current.ObjectId, out var target, out var authority) &&
                f.Registry.TryCommitPlayerMonsterControl(f.Session, target, authority, stun, f.Now, out _),
                "normal player controls remain available against a surviving Atlas");
            f.Now += TimeSpan.FromSeconds(4);
            current = f.Monster("atlas");
            Check.True(f.TryDirectHit(current, current.CurrentHealth, out var death) && death.Killed,
                "the optional Atlas fight can finish normally");
            f.Runtime.Map.TryGetWonderlandSnapshot(out var after);
            Check.True(after.State == WonderlandRunState.Completed && after.TerminalAt == complete.TerminalAt &&
                after.Clears.SequenceEqual(complete.Clears),
                "survivor death neither repeats titles nor extends the completion countdown");
            var bird = f.Monster("petbird", 3);
            f.Now = complete.TerminalAt!.Value + WonderlandCompletionPolicy.TreasureWindow;
            beforeHp = f.Character.CurrentHp;
            await f.IncomingAsync(bird);
            Check.True(f.Character.CurrentHp == beforeHp && !f.TryDirectHit(bird, 1, out _),
                "incoming and outgoing registry damage both reject the exact expired window");
        }
    }
}
