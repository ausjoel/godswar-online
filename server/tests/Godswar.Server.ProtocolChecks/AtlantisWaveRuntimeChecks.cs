using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Game.WorldInstances;

namespace Godswar.Server.ProtocolChecks;

internal static partial class AtlantisWaveRuntimeChecks
{
    public const string CheckName = "Atlantis exact wave publication, committed clears, and deadline gating";
    private static readonly DateTimeOffset Start = new(2026, 9, 8, 0, 0, 0, TimeSpan.Zero);

    public static Task RunAsync()
    {
        CheckPublicationAndIdentityGates();
        CheckCompletePlanProgression();
        CheckDeadlineAndCancellation();
        CheckConcurrentPublicationAndClear();
        CheckConcurrentDeadline();
        return Task.CompletedTask;
    }

    private static WorldInstanceDescriptor Descriptor() => WorldInstanceDescriptor.Create(
        RealmId.Tempest, WorldInstanceId.New(), new(205), InstanceKind.Dungeon, 5, Start);

    private static AtlantisWaveRuntime Create() => new(Descriptor(), Start);

    private static AtlantisWavePublication Pending(AtlantisWaveRuntime run, DateTimeOffset? at = null) =>
        run.PreviewPendingWave(at ?? Start) ?? throw new InvalidOperationException("Expected a pending wave.");

    private static AtlantisWaveMonsterIdentity[] Identities(AtlantisWavePublication wave, uint generation = 1) =>
        wave.Definition.Slots.Select(slot => new AtlantisWaveMonsterIdentity(
            checked((uint)(1_000 + wave.Token.WaveIndex * 12 + slot.SlotIndex)), generation, slot.Rank)).ToArray();

    private static AtlantisWaveKillResult Kill(AtlantisWaveRuntime run,
        AtlantisWaveMonsterIdentity identity, DateTimeOffset? at = null) =>
        run.RecordCommittedKill(run.Snapshot().WorldInstanceId,
            identity.ObjectId, identity.SpawnGeneration, at ?? Start);

    private static void CheckPublicationAndIdentityGates()
    {
        var run = Create();
        var pending = Pending(run);
        var before = run.Snapshot();
        Check.Equal(pending, Pending(run), "repeated preview preserves the same pending wave token");
        Check.Equal(before, run.Snapshot(), "preview does not bind or consume any monster identity");
        var bindings = Identities(pending);
        Check.True(run.BindPendingWave(pending.Token with { WorldInstanceId = WorldInstanceId.New() },
                bindings, Start.AddDays(1)).Outcome == AtlantisWaveBindOutcome.WrongInstance &&
            run.Snapshot() == before,
            "foreign publication cannot bind monsters or advance the owner clock");
        Check.True(run.BindPendingWave(pending.Token with { WaveIndex = 1 }, bindings, Start).Outcome ==
                AtlantisWaveBindOutcome.WrongWave,
            "a future wave cannot bypass the pending first group");
        var duplicate = bindings.ToArray();
        duplicate[1] = duplicate[0];
        var wrongRank = bindings.ToArray();
        wrongRank[10] = wrongRank[10] with { Rank = AtlantisMonsterRank.Normal };
        var emptyId = bindings.ToArray();
        emptyId[0] = emptyId[0] with { ObjectId = 0 };
        var emptyGeneration = bindings.ToArray();
        emptyGeneration[0] = emptyGeneration[0] with { SpawnGeneration = 0 };
        foreach (var invalid in new[] { bindings[..^1], duplicate, wrongRank, emptyId, emptyGeneration })
        {
            Check.True(run.BindPendingWave(pending.Token, invalid, Start).Outcome ==
                    AtlantisWaveBindOutcome.InvalidMonsterBindings && run.Snapshot() == before,
                "partial, duplicate, rank-mismatched, or empty identities leave publication pending");
        }

        Check.True(run.BindPendingWave(pending.Token, bindings, Start).Outcome == AtlantisWaveBindOutcome.Bound,
            "an exact complete ordered identity set binds the pending wave once");
        Check.True(run.TryGetActiveMonster(pending.Token.WorldInstanceId,
                bindings[10].ObjectId, 1, out var elite) && elite.Rank == AtlantisMonsterRank.Elite &&
            !run.TryGetActiveMonster(WorldInstanceId.New(), bindings[10].ObjectId, 1, out _),
            "active identity lookup supplies only this exact wave's pinned rank");
        Check.True(PendingOrNull(run) is null && Kill(run, bindings[0]).Outcome == AtlantisWaveKillOutcome.Applied,
            "an active wave cannot reveal its successor after only one kill");
        Check.True(run.BindPendingWave(pending.Token, bindings, Start).Outcome == AtlantisWaveBindOutcome.AlreadyBound &&
            run.Snapshot().RemainingMonsterCount == 11,
            "publication retry does not reset or resurrect an already committed kill");
        Check.True(run.BindPendingWave(pending.Token, bindings.Reverse().ToArray(), Start).Outcome ==
                AtlantisWaveBindOutcome.BindingConflict,
            "publication retry cannot change the ordered monster identities");
        Check.True(Kill(run, bindings[0]).Outcome == AtlantisWaveKillOutcome.DuplicateKill &&
            Kill(run, bindings[1] with { SpawnGeneration = 99 }).Outcome == AtlantisWaveKillOutcome.UnknownMonster &&
            run.Snapshot().CommittedKillCount == 1,
            "duplicate deaths and different generations cannot clear another monster");

        foreach (var identity in bindings.Skip(1))
        {
            _ = Kill(run, identity);
        }
        var next = Pending(run);
        Check.True(next.Token.WaveIndex == 1 && run.Snapshot() is
            { State: AtlantisWaveState.PendingPublication, CompletedWaveCount: 1, CommittedKillCount: 12 },
            "only all twelve committed deaths expose the second group");
        Check.True(run.BindPendingWave(pending.Token, bindings, Start).Outcome == AtlantisWaveBindOutcome.WrongWave &&
            run.BindPendingWave(next.Token, bindings, Start).Outcome == AtlantisWaveBindOutcome.IdentityReused,
            "a stale publication or reused old identity cannot create the next wave");
        var replacements = bindings.Select(static identity => identity with { SpawnGeneration = 2 }).ToArray();
        Check.True(run.BindPendingWave(next.Token, replacements, Start).Succeeded &&
            Kill(run, bindings[0]).Outcome == AtlantisWaveKillOutcome.UnknownMonster,
            "reusing an object with a new generation cannot accept a delayed old death");
        replacements[0] = replacements[0] with { ObjectId = 99 };
        Check.True(run.Snapshot().BoundMonsters[0].ObjectId == bindings[0].ObjectId,
            "caller mutation cannot alter the immutable published binding");
    }

    private static AtlantisWavePublication? PendingOrNull(AtlantisWaveRuntime run) => run.PreviewPendingWave(Start);

    private static void CheckCompletePlanProgression()
    {
        var descriptor = Descriptor();
        var run = new AtlantisWaveRuntime(descriptor, Start);
        var score = new AtlantisRunRuntime(descriptor, Start);
        for (var waveIndex = 0; waveIndex < 25; waveIndex++)
        {
            var at = Start.AddSeconds(waveIndex);
            var publication = Pending(run, at);
            Check.Equal(waveIndex, publication.Token.WaveIndex, "waves advance in approved order");
            var identities = Identities(publication);
            Check.True(run.BindPendingWave(publication.Token, identities, at).Succeeded,
                "each wave binds its actual live identities");
            Check.True(run.PreviewPendingWave(at) is null, "a bound group has no pending successor");
            if (waveIndex == 24)
            {
                Check.True(score.Snapshot().TeamPoints == 800 && identities.Length == 1 &&
                    publication.Definition.Slots[0].DisplayName == "Dinna the Sea Guard",
                    "800 points unlock only the final Sea Guard, not early completion");
            }
            for (var index = 0; index < identities.Length; index++)
            {
                var identity = identities[index];
                _ = score.RecordCommittedMonsterKill(descriptor.InstanceId, identity.ObjectId,
                    identity.SpawnGeneration, identity.Rank, at);
                var kill = Kill(run, identity, at);
                var expected = index != identities.Length - 1 ? AtlantisWaveKillOutcome.Applied :
                    waveIndex == 24 ? AtlantisWaveKillOutcome.RunCompleted : AtlantisWaveKillOutcome.WaveCleared;
                Check.True(kill.Outcome == expected, "only the last committed death clears its current group");
            }
        }
        var terminal = run.Snapshot();
        Check.True(terminal is { State: AtlantisWaveState.Completed, CompletedWaveCount: 25,
                CommittedKillCount: 245, RemainingMonsterCount: 0 } &&
            score.Snapshot() is { State: AtlantisRunState.Completed, TeamPoints: 850, ScoredKillCount: 245 },
            "all 245 approved deaths complete all 25 waves and exactly 850 score points together");
        Check.True(run.PreviewPendingWave(Start.AddHours(1)) is null &&
            run.Advance(Start.AddHours(1)) == terminal && run.Cancel(Start.AddHours(1)) == terminal,
            "completed waves remain terminal and expose no further publication");
    }

    private static void CheckDeadlineAndCancellation()
    {
        var pendingRun = Create();
        var publication = Pending(pendingRun);
        var deadline = pendingRun.Snapshot().Deadline;
        Check.True(pendingRun.PreviewPendingWave(deadline) is null &&
            pendingRun.Snapshot().State == AtlantisWaveState.TimedOut &&
            !pendingRun.BindPendingWave(publication.Token, Identities(publication), deadline).Succeeded,
            "a pending wave cannot publish or bind at the score deadline");

        var active = Create();
        publication = Pending(active);
        var identities = Identities(publication);
        _ = active.BindPendingWave(publication.Token, identities, Start);
        foreach (var identity in identities[..^1])
        {
            _ = Kill(active, identity, deadline.AddTicks(-1));
        }
        Check.True(Kill(active, identities[^1], deadline).Outcome == AtlantisWaveKillOutcome.TimedOut &&
            active.Snapshot() is { State: AtlantisWaveState.TimedOut, CompletedWaveCount: 0,
                CommittedKillCount: 11, RemainingMonsterCount: 1 } &&
            active.PreviewPendingWave(deadline.AddSeconds(1)) is null,
            "the final group death at the deadline cannot unlock a later wave");

        var between = Create();
        publication = Pending(between);
        identities = Identities(publication);
        _ = between.BindPendingWave(publication.Token, identities, Start);
        foreach (var identity in identities)
        {
            _ = Kill(between, identity, deadline.AddTicks(-1));
        }
        var next = Pending(between, deadline.AddTicks(-1));
        Check.True(between.BindPendingWave(next.Token, Identities(next), deadline).Outcome ==
                AtlantisWaveBindOutcome.TimedOut && between.Snapshot().BoundMonsters.IsEmpty,
            "publication prepared before the deadline cannot bind after it expires");

        var cancelled = Create();
        publication = Pending(cancelled);
        var stopped = cancelled.Cancel(Start.AddSeconds(1));
        Check.True(stopped.State == AtlantisWaveState.Cancelled &&
            cancelled.PreviewPendingWave(Start.AddSeconds(2)) is null &&
            !cancelled.BindPendingWave(publication.Token, Identities(publication), Start.AddSeconds(2)).Succeeded &&
            cancelled.Advance(deadline) == stopped,
            "an explicit terminal cancellation permanently cancels pending publication");
    }
}
