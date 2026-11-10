using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Game;
using Godswar.Server.Game.WorldInstances;

namespace Godswar.Server.ProtocolChecks;

internal static partial class AtlantisRunRuntimeChecks
{
    private static void CheckConcurrentKillAndClockDelivery()
    {
        var duplicates = Create();
        var duplicateResults = new AtlantisKillResult[64];
        Parallel.For(0, duplicateResults.Length, index => duplicateResults[index] = Kill(duplicates, 1));
        Check.Equal(1, duplicateResults.Sum(static result => result.PointsAwarded),
            "concurrent delivery of the same committed kill awards exactly once");
        Check.Equal(63, duplicateResults.Count(static result => result.Outcome == AtlantisKillOutcome.DuplicateKill),
            "all remaining concurrent deliveries are duplicates");

        var unique = Create();
        Parallel.For(1, 101, index => _ = Kill(unique, (uint)index));
        Check.True(unique.Snapshot() is { TeamPoints: 100, ScoredKillCount: 100 },
            "concurrent unique kills lose no team points");

        var threshold = Create();
        for (uint id = 1; id <= 84; id++)
        {
            _ = Kill(threshold, id, AtlantisMonsterRank.Elite);
        }
        var thresholdResults = new AtlantisKillResult[32];
        Parallel.For(0, thresholdResults.Length, index =>
            thresholdResults[index] = Kill(threshold, (uint)(100 + index), AtlantisMonsterRank.Elite));
        Check.Equal(1, thresholdResults.Count(static result => result.Outcome == AtlantisKillOutcome.Completed),
            "only one concurrent kill can publish the completion transition");
        Check.True(threshold.Snapshot() is { State: AtlantisRunState.Completed, TeamPoints: 850, ScoredKillCount: 85 },
            "concurrent kills after completion cannot inflate the terminal score");

        var instance = new MapInstance(Descriptor());
        var starts = new AtlantisRunSnapshot[32];
        Parallel.For(0, starts.Length, index =>
        {
            Check.True(instance.TryStartAtlantisEncounter(Start, out starts[index]),
                "concurrent admission can observe the one run");
        });
        Check.True(starts.All(snapshot => snapshot == starts[0]),
            "concurrent starts publish one identical initial state");

        for (var repetition = 0; repetition < 16; repetition++)
        {
            CheckKillClockRace(eligibleKill: false);
            CheckKillClockRace(eligibleKill: true);
        }
    }

    private static void CheckKillClockRace(bool eligibleKill)
    {
        var run = Create();
        for (uint id = 1; id <= 16; id++)
        {
            _ = Kill(run, id, AtlantisMonsterRank.Boss);
        }
        var deadline = run.Snapshot().Deadline;
        using var ready = new Barrier(2);
        var scoring = Task.Run(() =>
        {
            ready.SignalAndWait();
            return Kill(run, 17, AtlantisMonsterRank.Boss,
                at: eligibleKill ? deadline.AddTicks(-1) : deadline);
        });
        var clock = Task.Run(() =>
        {
            ready.SignalAndWait();
            return run.Advance(deadline);
        });
        Task.WaitAll(scoring, clock);
        var terminal = run.Snapshot();
        if (!eligibleKill)
        {
            Check.True(terminal is { State: AtlantisRunState.TimedOut, TeamPoints: 800 } &&
                scoring.Result.PointsAwarded == 0,
                "deadline kill cannot win regardless of whether scoring or the clock owns the lock first");
        }
        else
        {
            Check.True(terminal is { State: AtlantisRunState.Completed, TeamPoints: 850 } or
                { State: AtlantisRunState.TimedOut, TeamPoints: 800 },
                "racing eligible kill and expiry produce one consistent terminal state");
            Check.True((terminal.State == AtlantisRunState.Completed) ==
                (scoring.Result.Outcome == AtlantisKillOutcome.Completed),
                "completion outcome and terminal snapshot agree under the race");
        }
        _ = run.Advance(deadline.AddDays(1));
        _ = Kill(run, 18, AtlantisMonsterRank.Boss, at: deadline);
        Check.Equal(terminal, run.Snapshot(), "racing terminal outcome remains permanently frozen");
    }
}
