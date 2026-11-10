using Godswar.Server.Game;
using Godswar.Server.State;
using Godswar.Server.World.Systems.Monsters;

namespace Godswar.Server.ProtocolChecks;

internal static partial class MonsterEcsParityChecks
{
    public const string NativeControlsCheckName = "Monster native controls preserve movement attack and cast distinctions";
    public static Task RunNativeControlsAsync()
    {
        foreach (var skill in new[] { 74, 354, 604, 794 }) CheckControlParity(skill);
        CheckControlOverlapAndGeneration();
        return Task.CompletedTask;
    }

    private static void CheckControlParity(int skill)
    {
        Check.True(MonsterControlSkillPolicy.TryGet(skill, out var definition), "native control family exists");
        var spawn = CreateMonster(48001, 100, 50, tier: 30);
        IMonsterMapRuntime legacy = new MonsterMapRuntime(0, [spawn], Start);
        IMonsterMapRuntime ecs = new EcsMonsterMapRuntime(0, [spawn], Start);
        var target = new MonsterCombatTarget(731, 108, 50, true);
        Step(Start);
        Check.True(legacy.Snapshot().Single().IsMoving, "aggressive monster really chases before control");
        var at = Start.AddMilliseconds(50);
        Check.True(legacy.TryApplyControl(spawn.ObjectId, 731, definition, 1, at, out var left) && left.Applied &&
            ecs.TryApplyControl(spawn.ObjectId, 731, definition, 1, at, out var right) && right.Applied,
            "both runtimes accept current live monster control without boss immunity");
        var stopped = definition.Control.HasFlag(HostileStatusControlFlags.NonMoving);
        var blocksAttack = definition.Control.HasFlag(HostileStatusControlFlags.NonAttackUsing);
        Check.True(legacy.Snapshot().SequenceEqual(ecs.Snapshot()), "control snapshots preserve engine parity");
        Check.True(legacy.Snapshot().Single().IsMoving == !stopped &&
            legacy.Snapshot().Single().CurrentHealth == 237, "control changes movement, never invents damage");
        var pending = Step(at);
        Check.Equal(stopped ? 1 : 0, pending.Updates.Count(u => u.Kind == MonsterRuntimeUpdateKind.Arrived),
            "rooting a moving actor emits exactly one stop transition");
        for (var i = 1; i <= 5; i++)
        {
            var tick = Step(at.AddMilliseconds(i * 20));
            if (stopped) Check.True(tick.Updates.All(u => u.Kind is not (MonsterRuntimeUpdateKind.Started or MonsterRuntimeUpdateKind.Arrived)),
                "held control creates no movement or repeated stop spam");
        }
        var current = legacy.Snapshot().Single();
        target = target with { X = current.X, Z = current.Z };
        var attacks = new List<MonsterRuntimeUpdate>();
        for (var i = 1; i <= 10; i++) attacks.AddRange(Step(at.AddMilliseconds(100 + i * 100)).Updates);
        Check.Equal(!blocksAttack, attacks.Any(u => u.Kind == MonsterRuntimeUpdateKind.Attacked),
            "Frozen and Silence preserve in-range basic attacks; Stun and Caged block them");
        var flags = legacy.Snapshot().Single().ControlsAt(at.AddSeconds(1));
        Check.True(((flags & MonsterControlState.CastBlockFlags) != 0) == (skill != 354),
            "Frozen interrupts the old intonation but permits a new cast; the other controls lock skills");
        Check.True(legacy.Snapshot().Single().CastInterruptionRevision > 0,
            "every native family invalidates a prior spell windup");
        var resumed = new List<MonsterRuntimeUpdate>(Step(at + definition.Duration).Updates);
        Check.True(legacy.Snapshot().Single().ControlsAt(at + definition.Duration) == HostileStatusControlFlags.None,
            "controls expire exactly at their server deadline");
        for (var i = 1; i <= 4; i++) resumed.AddRange(Step(at + definition.Duration + TimeSpan.FromMilliseconds(i * 100)).Updates);
        Check.True(resumed.Any(u => u.Kind == MonsterRuntimeUpdateKind.Attacked),
            "new attacks resume after expiry without replaying a held windup");

        MonsterRuntimeTick Step(DateTimeOffset now)
        {
            var l = legacy.Advance(now, [target]); var r = ecs.Advance(now, [target]);
            Check.True(l.PositionsChanged == r.PositionsChanged && l.Updates.SequenceEqual(r.Updates) &&
                legacy.Snapshot().SequenceEqual(ecs.Snapshot()), "control advance remains deterministic across engines");
            return l;
        }
    }

    private static void CheckControlOverlapAndGeneration()
    {
        foreach (var mode in new[] { MonsterRuntimeMode.Legacy, MonsterRuntimeMode.Ecs })
        {
            var spawn = CreateMonster(48002, 0, 0, tier: 30);
            IMonsterMapRuntime runtime = mode == MonsterRuntimeMode.Legacy ?
                new MonsterMapRuntime(0, [spawn], Start) : new EcsMonsterMapRuntime(0, [spawn], Start);
            MonsterControlSkillPolicy.TryGet(354, out var freeze);
            MonsterControlSkillPolicy.TryGet(350, out var weaker);
            MonsterControlSkillPolicy.TryGet(604, out var silence);
            Check.True(runtime.TryApplyControl(spawn.ObjectId, 731, freeze, 1, Start, out var frozen) && frozen.Applied,
                "strong freeze applies");
            Check.True(runtime.TryApplyControl(spawn.ObjectId, 731, silence, 1, Start, out var silenced) && silenced.Applied,
                "independent silence coexists with freeze");
            var before = runtime.Snapshot().Single();
            Check.True(runtime.TryApplyControl(spawn.ObjectId, 731, weaker, 1, Start.AddSeconds(1), out var rejected) && !rejected.Applied,
                "a weaker same-kind control cannot erase the existing native priority");
            Check.True(before.Controls.Equals(runtime.Snapshot().Single().Controls), "rejected replacement changes no interruption fence");
            Check.True(before.Controls.Active(Start).Count() == 2 && before.ControlsAt(Start).HasFlag(HostileStatusControlFlags.NonMoving) &&
                before.ControlsAt(Start).HasFlag(HostileStatusControlFlags.NonMagicUsing), "overlapping controls retain both responsibilities");
            Check.True(runtime.TryApplyDamage(spawn.ObjectId, uint.MaxValue, 731, 1, Start.AddSeconds(2), out var killed) && killed.Killed,
                "controlled target can still die");
            Check.True(!killed.Monster.Controls.Active(Start.AddSeconds(2)).Any(), "death clears all native control entries");
            Check.True(runtime.TryApplyControl(spawn.ObjectId, 731, silence, 1, Start.AddSeconds(3), out var dead) && !dead.Applied,
                "corpses cannot receive fresh controls");
            // Lifecycle updates preserve separate death, despawn and respawn
            // publications even if the test clock jumps over both deadlines.
            runtime.Advance(Start.AddSeconds(2));
            runtime.Advance(Start.AddMinutes(1)); runtime.Advance(Start.AddMinutes(2));
            Check.True(runtime.Snapshot().Single() is { IsAlive: true, IsSpawned: true, SpawnGeneration: 2 },
                "real lifecycle publishes a fresh living generation before the stale-control attempt");
            Check.True(!runtime.TryApplyControl(spawn.ObjectId, 731, silence, 1, Start.AddMinutes(2), out _),
                "respawn generation rejects old control authority");
        }
    }
}
