using System.Data.Common;
using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Domain.World.Instances;

namespace Godswar.Server.ProtocolChecks;

/// <summary>
/// S5b (5 Nov party run b87f263a): a settlement rejection that can never succeed must stop retrying after a bounded
/// number of attempts, be parked once with its evidence, and let the run retire. A transient failure keeps
/// retrying (and holds the run) until the hold limit.
/// </summary>
internal static partial class InstanceCallerHandlerChecks
{
    public const string BoundedSettlementRetriesCheckName =
        "Wonderland and Atlantis settlements park a deterministic rejection and let the run retire";

    public static async Task RunBoundedSettlementRetriesAsync()
    {
        CheckSettlementFailureClassification();
        await CheckWonderlandDeterministicTitleParksAsync();
        await CheckWonderlandTransientTitleKeepsRetryingAsync();
        await CheckAtlantisDeterministicCompletionParksAsync();
    }

    private static void CheckSettlementFailureClassification()
    {
        Check.True(SettlementRetryPolicy.Classify(new SettlementRejectedException("AdmissionConflict", "x")) ==
                SettlementFailureKind.Deterministic &&
            SettlementRetryPolicy.Classify(new InvalidDataException(
                "Legacy-instance admissions did not match the reserved party members exactly.")) ==
                SettlementFailureKind.Deterministic &&
            SettlementRetryPolicy.Classify(new ScriptedDbException("23514", transient: false)) ==
                SettlementFailureKind.Deterministic,
            "store rejections, the 5 Nov ledger mismatch and a CHECK violation are deterministic");
        Check.True(SettlementRetryPolicy.Classify(new IOException("db down")) == SettlementFailureKind.Transient &&
            SettlementRetryPolicy.Classify(new TimeoutException()) == SettlementFailureKind.Transient &&
            SettlementRetryPolicy.Classify(new ScriptedDbException("08006", transient: false)) ==
                SettlementFailureKind.Transient &&
            SettlementRetryPolicy.Classify(new ScriptedDbException("23505", transient: true)) ==
                SettlementFailureKind.Transient &&
            SettlementRetryPolicy.Classify(new InvalidOperationException("wrapped", new IOException("io"))) ==
                SettlementFailureKind.Transient,
            "I/O, timeouts, connection loss and driver-flagged transient errors keep retrying");
    }

    private static async Task CheckWonderlandDeterministicTitleParksAsync()
    {
        await using var fixture = await CreateWonderlandHandlerFixtureAsync();
        var leader = fixture.Party.Leader;
        var registry = leader.Registry;
        var runtime = await EnterWonderlandHandlerAsync(fixture);
        var parkedBefore = ParkedSettlements.Total;
        fixture.Titles.RejectWith = WonderlandTitleStatus.AdmissionConflict;
        await ClearWonderlandHandlerIslandAsync(fixture, runtime);
        var start = WonderlandNow(runtime);
        Check.True(fixture.Titles.Requests.Count == 1 && registry.HasPendingWonderlandTitles(runtime.InstanceId),
            "the rejected island-one title is pending after its first attempt");

        await InvokeAsync(leader.Handler, CreateRepetitionLeave(227, 0)); // the leader ends the run
        await registry.AdvanceMonsterWorldOnceAsync(start.AddSeconds(10), CancellationToken.None);
        await registry.AdvanceMonsterWorldOnceAsync(start.AddSeconds(20), CancellationToken.None);
        Check.True(fixture.Titles.Requests.Count == 3 && registry.HasPendingWonderlandTitles(runtime.InstanceId) &&
            registry.TryGetWorldInstance(runtime.InstanceId, out _) && ParkedSettlements.Total == parkedBefore,
            "inside the first minute a rejection is retried and still holds the run (read-after-write grace)");

        await registry.AdvanceMonsterWorldOnceAsync(start.AddSeconds(70), CancellationToken.None);
        var attemptsAtPark = fixture.Titles.Requests.Count;
        Check.True(attemptsAtPark == 4 && !registry.HasPendingWonderlandTitles(runtime.InstanceId) &&
            ParkedSettlements.Total == parkedBefore + 1,
            $"after {SettlementRetryPolicy.DeterministicAttempts}+ attempts and a minute the rejection is parked " +
            $"(attempts {attemptsAtPark}, parked {ParkedSettlements.Total - parkedBefore})");
        var parked = ParkedSettlements.Snapshot().Last();
        Check.True(parked.Kind == "wonderland-title" && parked.Failure == SettlementFailureKind.Deterministic &&
            !parked.RetryingInBackground && parked.Key == $"{runtime.InstanceId}/island-1" &&
            parked.Evidence.Contains("status=AdmissionConflict", StringComparison.Ordinal) &&
            parked.Evidence.Contains($"party-reservation={fixture.Daily.Claims.Single().ReservationId}",
                StringComparison.Ordinal) &&
            parked.Evidence.Contains($"paid=[{leader.Character.Id}@{leader.Character.AccountId}]", StringComparison.Ordinal),
            "the parked record carries instance, island, status, roster and reservations: " + parked.Evidence);
        Check.True(ParkedSettlements.DescribeJson().Contains(parked.Key, StringComparison.Ordinal),
            "the management /settlement/parked payload lists it for the health-check alert");

        await registry.AdvanceMonsterWorldOnceAsync(start.AddSeconds(80), CancellationToken.None);
        await registry.AdvanceMonsterWorldOnceAsync(start.AddSeconds(200), CancellationToken.None);
        await registry.AdvanceMonsterWorldOnceAsync(start.AddMinutes(30), CancellationToken.None);
        Check.True(fixture.Titles.Requests.Count == attemptsAtPark && ParkedSettlements.Total == parkedBefore + 1,
            "a parked rejection is never retried or re-alerted");
        Check.True(!registry.TryGetWorldInstance(runtime.InstanceId, out _) && leader.Character.CurrentMap != 207 &&
            registry.ForgetSettledWonderlandTitles(runtime.InstanceId),
            "the run exits its members and retires");
    }

    private static async Task CheckWonderlandTransientTitleKeepsRetryingAsync()
    {
        await using var fixture = await CreateWonderlandHandlerFixtureAsync();
        var leader = fixture.Party.Leader;
        var registry = leader.Registry;
        var runtime = await EnterWonderlandHandlerAsync(fixture);
        var parkedBefore = ParkedSettlements.Total;
        fixture.Titles.FailuresRemaining = int.MaxValue;
        await ClearWonderlandHandlerIslandAsync(fixture, runtime);
        var start = WonderlandNow(runtime);
        await InvokeAsync(leader.Handler, CreateRepetitionLeave(227, 0));
        foreach (var seconds in new[] { 10, 20, 70, 130, 300 })
            await registry.AdvanceMonsterWorldOnceAsync(start.AddSeconds(seconds), CancellationToken.None);
        Check.True(fixture.Titles.Requests.Count == 6 && registry.HasPendingWonderlandTitles(runtime.InstanceId) &&
            registry.TryGetWorldInstance(runtime.InstanceId, out _) && ParkedSettlements.Total == parkedBefore,
            $"a transient failure keeps retrying and holds the run (attempts {fixture.Titles.Requests.Count})");

        await registry.AdvanceMonsterWorldOnceAsync(start.Add(SettlementRetryPolicy.TransientHoldLimit).AddSeconds(1),
            CancellationToken.None);
        await registry.AdvanceMonsterWorldOnceAsync(start.Add(SettlementRetryPolicy.TransientHoldLimit).AddSeconds(2),
            CancellationToken.None);
        Check.True(!registry.HasPendingWonderlandTitles(runtime.InstanceId) && ParkedSettlements.Total == parkedBefore + 1 &&
            ParkedSettlements.Snapshot().Last().RetryingInBackground &&
            !registry.TryGetWorldInstance(runtime.InstanceId, out _),
            "past the hold limit it is parked for the alert and the run retires");

        fixture.Titles.FailuresRemaining = 0;
        var before = fixture.Titles.Requests.Count;
        await registry.AdvanceMonsterWorldOnceAsync(start.Add(SettlementRetryPolicy.TransientHoldLimit).AddMinutes(2),
            CancellationToken.None);
        Check.True(fixture.Titles.Requests.Count == before + 1 && fixture.Titles.AppliedReceipts.Count == 1 &&
            ParkedSettlements.Snapshot().Last().RecoveredAt is not null &&
            !registry.IsWonderlandTitleParked(runtime.InstanceId, 1),
            "the background retry settles the title once the outage ends");
        await registry.AdvanceMonsterWorldOnceAsync(start.Add(SettlementRetryPolicy.TransientHoldLimit).AddMinutes(5),
            CancellationToken.None);
        Check.True(fixture.Titles.Requests.Count == before + 1, "and stops retrying after it settled");
    }

    private static async Task CheckAtlantisDeterministicCompletionParksAsync()
    {
        await using var fixture = await CreateAtlantisOpalFixtureAsync(null, null, partySize: 1);
        var leader = fixture.Leader;
        var registry = leader.Registry;
        var store = new ScriptedAtlantisCompletionRewards(fixture.Characters) { RejectAttemptsRemaining = int.MaxValue };
        registry.ConfigureAtlantisCompletionRewards(store);
        registry.RegisterAuthoritativeInstanceTransitionSink(leader.Session,
            (command, token) => InvokeAuthoritativeTransitionAsync(leader.Handler, command, token));
        try
        {
            var parkedBefore = ParkedSettlements.Total;
            var honourBefore = leader.Character.MedusaHonorPoints;
            var titlesBefore = leader.Character.OwnedTitleIds.ToArray();
            var (runtime, initial) = await PrepareAtlantisDepartureAsync(fixture);
            var completed = CompleteAtlantisRewardRun(runtime, initial);
            var due = completed.TerminalAt!.Value.AddSeconds(30);
            await registry.AdvanceMonsterWorldOnceAsync(due, CancellationToken.None);
            await registry.AdvanceMonsterWorldOnceAsync(due.AddSeconds(10), CancellationToken.None);
            await registry.AdvanceMonsterWorldOnceAsync(due.AddSeconds(20), CancellationToken.None);
            Check.True(store.CommitCount == 0 && runtime.Map.Population == 1 &&
                registry.TryGetWorldInstance(runtime.InstanceId, out _) && ParkedSettlements.Total == parkedBefore,
                "inside the first minute an Atlantis rejection is retried and holds the member and runtime");
            var firstMinute = store.Requests.Count;
            await registry.AdvanceMonsterWorldOnceAsync(due.AddSeconds(70), CancellationToken.None);
            await registry.AdvanceMonsterWorldOnceAsync(due.AddSeconds(71), CancellationToken.None);
            var atPark = store.Requests.Count;
            Check.True(atPark == firstMinute + 1 && ParkedSettlements.Total == parkedBefore + 1 &&
                registry.IsAtlantisCompletionRewardParked(runtime.InstanceId) &&
                ParkedSettlements.Snapshot().Last().Evidence.Contains("status=CharacterUnavailable", StringComparison.Ordinal),
                $"the rejection is parked once with its evidence (requests {firstMinute} -> {atPark})");
            await registry.AdvanceMonsterWorldOnceAsync(due.AddSeconds(120), CancellationToken.None);
            await registry.AdvanceMonsterWorldOnceAsync(due.AddMinutes(30), CancellationToken.None);
            Check.True(store.Requests.Count == atPark && store.CommitCount == 0 &&
                ParkedSettlements.Total == parkedBefore + 1,
                "a parked Atlantis rejection is never retried");
            Check.True(leader.Character.CurrentMap != 205 && runtime.Map.Population == 0 &&
                !registry.TryGetWorldInstance(runtime.InstanceId, out _),
                "the member is carried out and the Atlantis runtime retires");
            Check.True(leader.Character.MedusaHonorPoints == honourBefore &&
                leader.Character.OwnedTitleIds.Order().SequenceEqual(titlesBefore.Order()),
                "a parked completion grants no honour or title in memory (nothing was awarded)");
        }
        finally
        {
            registry.UnregisterAuthoritativeInstanceTransitionSink(leader.Session);
        }
    }

    private sealed class ScriptedDbException(string sqlState, bool transient) : DbException("scripted " + sqlState)
    {
        public override string SqlState => sqlState;
        public override bool IsTransient => transient;
    }
}
