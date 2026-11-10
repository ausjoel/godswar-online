using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Game;
using Godswar.Server.Game.WorldInstances;

namespace Godswar.Server.ProtocolChecks;

internal static partial class AtlantisRunRuntimeChecks
{
    public const string CheckName = "Atlantis exact-instance score, deadline, duplicate, and terminal runtime";
    private static readonly DateTimeOffset Start = new(2026, 9, 8, 0, 0, 0, TimeSpan.Zero);

    public static Task RunAsync()
    {
        CheckScoresAndKillIdentity();
        CheckCompletionThresholdAndFreeze();
        CheckExclusiveDeadlineAndClock();
        CheckMapOwnershipAndExplicitStart();
        CheckConcurrentKillAndClockDelivery();
        return Task.CompletedTask;
    }

    internal static WorldInstanceDescriptor Descriptor(
        short mapId = 205, InstanceKind kind = InstanceKind.Dungeon) =>
        WorldInstanceDescriptor.Create(RealmId.Tempest, WorldInstanceId.New(),
            new(mapId), kind, 5, Start);

    private static AtlantisRunRuntime Create() => new(Descriptor(), Start);

    private static AtlantisKillResult Kill(AtlantisRunRuntime run, uint objectId,
        AtlantisMonsterRank rank = AtlantisMonsterRank.Normal, uint generation = 1,
        DateTimeOffset? at = null) => run.RecordCommittedMonsterKill(
            run.Snapshot().WorldInstanceId, objectId, generation, rank, at ?? Start.AddSeconds(1));

    private static void CheckScoresAndKillIdentity()
    {
        var run = Create();
        Check.Equal(1, Kill(run, 1).PointsAwarded, "normal committed kill scores one");
        Check.Equal(10, Kill(run, 2, AtlantisMonsterRank.Elite).PointsAwarded,
            "elite committed kill scores ten");
        Check.Equal(50, Kill(run, 3, AtlantisMonsterRank.Boss).PointsAwarded,
            "boss committed kill scores fifty");
        Check.True(Kill(run, 3, AtlantisMonsterRank.Normal).Outcome == AtlantisKillOutcome.DuplicateKill,
            "replayed identity cannot score again with a different rank");
        Check.Equal(61, run.Snapshot().TeamPoints, "all three ranks contribute to the same team score");
        Check.Equal(1, Kill(run, 1, generation: 2).PointsAwarded,
            "a distinct committed spawn generation is a distinct kill");

        Check.True(Kill(run, 4, (AtlantisMonsterRank)255).Outcome == AtlantisKillOutcome.UnknownRank,
            "unknown rank receives no points");
        Check.Equal(1, Kill(run, 4).PointsAwarded, "an invalid rank does not consume the kill identity");
        Check.True(Kill(run, 0).Outcome == AtlantisKillOutcome.InvalidMonsterIdentity &&
            Kill(run, 5, generation: 0).Outcome == AtlantisKillOutcome.InvalidMonsterIdentity,
            "empty monster identities cannot score");
        Check.Equal(63, run.Snapshot().TeamPoints, "invalid identities and ranks do not add points");

        var before = run.Snapshot();
        var foreign = run.RecordCommittedMonsterKill(WorldInstanceId.New(), 5, 1,
            AtlantisMonsterRank.Boss, before.Deadline.AddDays(1));
        Check.True(foreign.Outcome == AtlantisKillOutcome.WrongInstance,
            "another world instance cannot submit a kill");
        Check.Equal(before, run.Snapshot(), "foreign instance cannot advance the owner clock");
        var other = Create();
        Check.Equal(50, Kill(other, 3, AtlantisMonsterRank.Boss).PointsAwarded,
            "a separate dungeon owns independent deduplication and score");
    }

    private static void CheckCompletionThresholdAndFreeze()
    {
        var run = Create();
        for (uint id = 1; id <= 16; id++)
        {
            _ = Kill(run, id, AtlantisMonsterRank.Boss);
        }
        for (uint id = 17; id <= 65; id++)
        {
            _ = Kill(run, id);
        }
        Check.True(run.Snapshot() is { State: AtlantisRunState.Active, TeamPoints: 849 },
            "849 team points leaves the run active");
        var winningAt = run.Snapshot().Deadline.AddTicks(-1);
        var winning = Kill(run, 66, AtlantisMonsterRank.Elite, at: winningAt);
        Check.True(winning.Outcome == AtlantisKillOutcome.Completed && winning.PointsAwarded == 10,
            "a threshold crossing retains the full kill score");
        var completed = run.Snapshot();
        Check.True(completed is { State: AtlantisRunState.Completed, TeamPoints: 859 } &&
            completed.TerminalAt == winningAt, "completion at the last eligible tick freezes its timestamp");
        _ = Kill(run, 67, AtlantisMonsterRank.Boss, at: completed.Deadline);
        _ = run.Advance(Start);
        _ = run.Advance(completed.Deadline.AddDays(1));
        Check.Equal(completed, run.Snapshot(), "completion is frozen across later kills and clock movement");

        var exact = Create();
        for (uint id = 1; id <= 17; id++)
        {
            _ = Kill(exact, id, AtlantisMonsterRank.Boss);
        }
        Check.True(exact.Snapshot() is { State: AtlantisRunState.Completed, TeamPoints: 850 },
            "exactly 850 team points completes the run");
    }

    private static void CheckExclusiveDeadlineAndClock()
    {
        foreach (var ticksAfter in new[] { 0L, 1L })
        {
            var run = Create();
            for (uint id = 1; id <= 16; id++)
            {
                _ = Kill(run, id, AtlantisMonsterRank.Boss);
            }
            var deadline = run.Snapshot().Deadline;
            var rejected = Kill(run, 17, AtlantisMonsterRank.Boss, at: deadline.AddTicks(ticksAfter));
            Check.True(rejected.Outcome == AtlantisKillOutcome.TimedOut && rejected.PointsAwarded == 0,
                "a winning kill at or after the deadline cannot complete the run");
            var timedOut = run.Snapshot();
            Check.True(timedOut is { State: AtlantisRunState.TimedOut, TeamPoints: 800 } &&
                timedOut.TerminalAt == deadline && timedOut.LastObservedAt == deadline,
                "timeout retains score and fixes terminal time to the forty-minute deadline");
            _ = Kill(run, 18, AtlantisMonsterRank.Boss, at: deadline.AddTicks(-1));
            _ = run.Advance(deadline.AddHours(1));
            Check.Equal(timedOut, run.Snapshot(), "late delivery of an earlier kill cannot reopen a timed-out run");
        }

        var monotonic = Create();
        var current = monotonic.Advance(Start.AddMinutes(10));
        Check.Equal(current, monotonic.Advance(Start), "clock cannot move backward");
        Check.True(Kill(monotonic, 1, at: Start.AddMinutes(9)).Outcome ==
            AtlantisKillOutcome.TimestampMovedBackward, "backdated scoring cannot rewind authoritative time");
        Check.Equal(current, monotonic.Snapshot(), "rejected backdated kill does not change score or clock");
        Check.True(monotonic.Advance(current.Deadline.AddTicks(-1)).State == AtlantisRunState.Active,
            "clock remains active one tick before deadline");
        Check.True(monotonic.Advance(current.Deadline).State == AtlantisRunState.TimedOut,
            "clock reaches timeout exactly at deadline without needing another kill");
    }

    private static void CheckMapOwnershipAndExplicitStart()
    {
        var instance = new MapInstance(Descriptor());
        Check.True(!instance.TryGetAtlantisRunSnapshot(out _) &&
            !instance.TryAdvanceAtlantisEncounter(Start, out _), "reading or ticking cannot implicitly start a run");
        Check.True(instance.RecordCommittedAtlantisMonsterKill(instance.WorldInstanceId, 1, 1,
            "boss", Start).Outcome == AtlantisKillOutcome.RunNotStarted,
            "a kill cannot implicitly start a run");
        Check.True(instance.TryStartAtlantisEncounter(Start, out var initial), "entry starts its exact dungeon run");
        Check.Equal(TimeSpan.FromMinutes(40), initial.Deadline - initial.StartedAt,
            "adapter installs the policy duration");
        _ = instance.RecordCommittedAtlantisMonsterKill(instance.WorldInstanceId, 1, 1, "BOSS", Start);
        Check.True(instance.TryStartAtlantisEncounter(Start.AddMinutes(20), out var repeated) &&
            repeated.TeamPoints == 50 && repeated.Deadline == initial.Deadline,
            "repeated entry cannot reset score or extend the clock");
        Check.True(instance.TryAdvanceAtlantisEncounter(initial.Deadline, out var terminal) &&
            instance.TryStartAtlantisEncounter(initial.Deadline.AddHours(1), out var restarted) &&
            terminal == restarted, "explicit start cannot revive a terminal run");
        Check.True(!new MapInstance(205).TryStartAtlantisEncounter(DateTimeOffset.UtcNow, out _),
            "the open-world map-205 instance is not an Atlantis dungeon");
        Check.True(!new MapInstance(Descriptor(204)).TryStartAtlantisEncounter(Start, out _),
            "another dungeon map cannot attach an Atlantis run");
    }
}
