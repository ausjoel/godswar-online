using Godswar.Server.Game.WorldInstances;

namespace Godswar.Server.ProtocolChecks;

internal static partial class AtlantisWaveRuntimeChecks
{
    private static void CheckConcurrentPublicationAndClear()
    {
        var run = Create();
        var previews = new AtlantisWavePublication?[32];
        Parallel.For(0, previews.Length, index => previews[index] = run.PreviewPendingWave(Start));
        var publication = previews[0]!;
        Check.True(previews.All(candidate => candidate == publication) &&
            run.Snapshot().CommittedKillCount == 0,
            "concurrent previews share one pending publication without consuming it");
        var identities = Identities(publication);
        var bindings = new AtlantisWaveBindResult[32];
        Parallel.For(0, bindings.Length, index =>
            bindings[index] = run.BindPendingWave(publication.Token, identities, Start));
        Check.True(bindings.Count(static result => result.Outcome == AtlantisWaveBindOutcome.Bound) == 1 &&
            bindings.Count(static result => result.Outcome == AtlantisWaveBindOutcome.AlreadyBound) == 31 &&
            run.Snapshot().RemainingMonsterCount == 12,
            "concurrent publication retries install one complete wave binding");

        var duplicates = new AtlantisWaveKillResult[32];
        Parallel.For(0, duplicates.Length, index => duplicates[index] = Kill(run, identities[0]));
        Check.True(duplicates.Count(static result => result.Outcome == AtlantisWaveKillOutcome.Applied) == 1 &&
            duplicates.Count(static result => result.Outcome == AtlantisWaveKillOutcome.DuplicateKill) == 31 &&
            run.Snapshot().RemainingMonsterCount == 11,
            "concurrent duplicate committed deaths consume one current monster");
        var remaining = new AtlantisWaveKillResult[11];
        Parallel.For(0, remaining.Length, index => remaining[index] = Kill(run, identities[index + 1]));
        Check.True(remaining.Count(static result => result.Outcome == AtlantisWaveKillOutcome.WaveCleared) == 1 &&
            remaining.Count(static result => result.Outcome == AtlantisWaveKillOutcome.Applied) == 10 &&
            run.Snapshot() is { State: AtlantisWaveState.PendingPublication, WaveIndex: 1,
                CompletedWaveCount: 1, CommittedKillCount: 12 },
            "racing final deaths advance one wave exactly once");
        Check.True(run.BindPendingWave(publication.Token, identities, Start).Outcome ==
                AtlantisWaveBindOutcome.WrongWave && Pending(run).Token.WaveIndex == 1,
            "a delayed previous-wave publication cannot roll back the new pending token");
    }

    private static void CheckConcurrentDeadline()
    {
        for (var repetition = 0; repetition < 8; repetition++)
        {
            CheckLastKillDeadlineRace(eligibleKill: false);
            CheckLastKillDeadlineRace(eligibleKill: true);
        }
    }

    private static void CheckLastKillDeadlineRace(bool eligibleKill)
    {
        var run = Create();
        var publication = Pending(run);
        var identities = Identities(publication);
        _ = run.BindPendingWave(publication.Token, identities, Start);
        var deadline = run.Snapshot().Deadline;
        foreach (var identity in identities[..^1])
        {
            _ = Kill(run, identity, deadline.AddTicks(-1));
        }
        using var ready = new Barrier(2);
        var clearing = Task.Run(() =>
        {
            ready.SignalAndWait();
            return Kill(run, identities[^1], eligibleKill ? deadline.AddTicks(-1) : deadline);
        });
        var expiring = Task.Run(() =>
        {
            ready.SignalAndWait();
            return run.Advance(deadline);
        });
        Task.WaitAll(clearing, expiring);
        var terminal = run.Snapshot();
        Check.True(terminal.State == AtlantisWaveState.TimedOut && terminal.TerminalAt == deadline &&
            run.PreviewPendingWave(deadline) is null,
            "a racing clock always cancels any unbound next wave at the deadline");
        if (eligibleKill)
        {
            Check.True(terminal.CompletedWaveCount is 0 or 1 &&
                terminal.CommittedKillCount == 11 + terminal.CompletedWaveCount,
                "the last eligible kill either clears once before timeout or loses the clock race");
        }
        else
        {
            Check.True(terminal.CompletedWaveCount == 0 && terminal.CommittedKillCount == 11 &&
                !clearing.Result.Accepted,
                "an exactly-deadline death cannot clear its wave in either lock order");
        }
    }
}
