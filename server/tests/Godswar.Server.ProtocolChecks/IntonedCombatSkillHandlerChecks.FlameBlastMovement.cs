using System.Reflection;
using Godswar.Server.Game;
using Godswar.Server.Game.WorldInstances;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class IntonedCombatSkillHandlerChecks
{
    private static async Task CheckFlameTargetsEnterAndLeaveAsync(PlayerRuntimeMode mode)
    {
        var clock = NewFlameClock();
        await using var fixture = await CreateFlameFixtureAsync(mode, clock, aggressive: true);
        fixture.Character.PositionX = -3;
        fixture.Registry.UpdateCharacter(fixture.Socket.Session, fixture.Character,
            advanceWorldRevision: true);
        Check.Equal(0, await CastAndReadInitialFlameAsync(fixture, -3),
            "the movement scenario begins with a real paid but empty ground cast");
        await WaitForFlameTimersAsync(fixture, clock, 1);

        // Drive the existing AI under its world owner. The fixture deliberately
        // leaves attack publication to the handler; these steps exercise actual
        // monster movement, without rewriting immutable spawn definitions.
        var aiTime = clock.GetUtcNow();
        AdvanceFlameAiUntil(fixture, ref aiTime, snapshots =>
            snapshots.Take(2).All(monster => InFlameField(monster, -3)));
        var entering = FlameMonsterSnapshots(fixture);
        Check.True(entering.Take(2).All(monster => InFlameField(monster, -3)) &&
            !InFlameField(entering[2], -3),
            "two live monsters enter the original field while the third stays outside");
        var pulse = await AdvanceAndReadFlameAsync(fixture, clock, TimeSpan.FromSeconds(1));
        Check.True(pulse.Damage > 0, "a later pulse acquires monsters absent from the initial placement");

        fixture.Character.PositionX = 20;
        fixture.Registry.UpdateCharacter(fixture.Socket.Session, fixture.Character,
            advanceWorldRevision: true);
        AdvanceFlameAiUntil(fixture, ref aiTime, snapshots =>
            snapshots.All(monster => !InFlameField(monster, -3)));
        var before = FlameMonsterHealth(fixture);
        var excluded = await AdvanceAndReadFlameAsync(fixture, clock, TimeSpan.FromSeconds(1));
        Check.True(excluded.Damage == 0,
            "monsters that leave the field are excluded from its next pulse");
        Check.True(before.SequenceEqual(FlameMonsterHealth(fixture)) &&
            fixture.Character.CurrentHp == 50 + pulse.Targets * FlameHealing,
            "neither a former target nor the caster's new position creates phantom damage or healing");
    }

    private static bool InFlameField(MonsterRuntimeSnapshot monster, float centerX) =>
        (monster.X - centerX) * (monster.X - centerX) + monster.Z * monster.Z < 16;

    private static MonsterRuntimeSnapshot[] FlameMonsterSnapshots(Fixture fixture) =>
        Enumerable.Range(0, 3).Select(index =>
        {
            Check.True(fixture.Registry.TryGetMonsterSnapshot(0, MonsterObjectId + (uint)index,
                out var monster), "moving targets retain their authoritative monster snapshots");
            return monster;
        }).ToArray();

    private static void AdvanceFlameAiUntil(Fixture fixture, ref DateTimeOffset now,
        Func<MonsterRuntimeSnapshot[], bool> condition)
    {
        var directory = (LocalWorldInstanceRuntimeDirectory)typeof(GameSessionRegistry)
            .GetProperty("WorldInstances", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(fixture.Registry)!;
        var runtime = directory.Snapshot().Single(instance => instance.MapId == 0);
        for (var step = 0; step < 120; step++)
        {
            if (condition(FlameMonsterSnapshots(fixture))) return;
            now += TimeSpan.FromMilliseconds(250);
            _ = MonsterOwnerCheckSteps.Advance(runtime, fixture.Registry, now);
        }
        Check.True(condition(FlameMonsterSnapshots(fixture)),
            "normal monster AI reaches the required field boundary within a bounded number of owner ticks");
    }
}
