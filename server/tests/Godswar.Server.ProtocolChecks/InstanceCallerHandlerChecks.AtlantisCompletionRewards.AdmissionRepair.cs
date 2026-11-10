using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Game;

namespace Godswar.Server.ProtocolChecks;

internal static partial class InstanceCallerHandlerChecks
{
    private static async Task CheckAtlantisCompletionRepairsAdmissionMarkersAsync()
    {
        // Entry records each successful member independently, with three
        // attempts each. Fail all six writes and the first completion repair.
        var daily = new ScriptedLegacyInstanceDailyEntryStore { AdmissionFailuresRemaining = 7 };
        await using var fixture = await CreateAtlantisOpalFixtureAsync(daily, null, partySize: 2);
        var leader = fixture.Leader;
        var registry = leader.Registry;
        var rewards = new ScriptedAtlantisCompletionRewards(fixture.Characters);
        registry.ConfigureAtlantisCompletionRewards(rewards, daily);
        registry.RegisterAuthoritativeInstanceTransitionSink(leader.Session,
            (command, token) => InvokeAuthoritativeTransitionAsync(leader.Handler, command, token));
        try
        {
            var (runtime, initial) = await PrepareAtlantisDepartureAsync(fixture);
            Check.True(daily.AdmissionAttempts.Count == 6 && daily.Admissions.Count == 0 &&
                runtime.Map.Population == 2,
                "both members entered even though all three durable marker attempts failed for each");
            var admittedIds = fixture.Characters.Select(character => character.Id).ToHashSet();
            var reservation = daily.AdmissionAttempts[0].ReservationId;
            Check.True(daily.AdmissionAttempts.All(attempt => attempt.ReservationId == reservation) &&
                daily.AdmissionAttempts.SelectMany(attempt => attempt.CharacterIds).ToHashSet().SetEquals(admittedIds),
                "entry failures retain the exact successful original admission identities");

            var follower = fixture.Followers.Single();
            Check.True(registry.TryTransferMap(follower.Session, 205, 0, 136f, -150f),
                "one admitted member leaves before completion, making repair and reward rosters different");
            var completed = CompleteAtlantisRewardRun(runtime, initial);
            var evidence = runtime.Map.GetAtlantisCompletionEvidence()!.Request!;
            Check.True(evidence.AdmissionReservationId == reservation && evidence.AdmittedCharacterIds.Count == 2 &&
                evidence.CharacterIds.SequenceEqual(new[] { leader.Character.Id }),
                "completion freezes the original admission roster separately from its eligible finisher");
            var due = completed.TerminalAt!.Value.AddSeconds(30);
            var before = AtlantisRewardState(leader.Character);
            await registry.AdvanceMonsterWorldOnceAsync(due, CancellationToken.None);
            Check.True(daily.AdmissionAttempts.Count == 7 && daily.Admissions.Count == 0 &&
                rewards.Requests.Count == 0 && AtlantisRewardState(leader.Character) == before &&
                leader.Character.CurrentMap == 205 && runtime.Map.Population == 1,
                "a failed completion repair blocks reward settlement and overdue automatic exit");
            Check.True(daily.AdmissionAttempts[^1].ReservationId == reservation &&
                daily.AdmissionAttempts[^1].CharacterIds.SetEquals(admittedIds),
                "completion repairs the same reservation and original admitted roster, not just current finishers");

            await registry.AdvanceMonsterWorldOnceAsync(due.AddSeconds(1), CancellationToken.None);
            Check.True(daily.AdmissionAttempts.Count == 8 && daily.Admissions.Count == 1 &&
                daily.Admissions[0].ReservationId == reservation &&
                daily.Admissions[0].CharacterIds.SetEquals(admittedIds) &&
                rewards.Requests.Count == 1 && rewards.CommitCount == 1 &&
                rewards.Requests[0].RequestHash == evidence.RequestHash &&
                rewards.Committed(runtime.InstanceId).Members.Count == 1 &&
                leader.Character.OwnedTitleIds.Contains(AtlantisCompletionRewardPolicy.SeabedExplorerTitleId) &&
                leader.Character.CurrentMap != 205 && !registry.TryGetWorldInstance(runtime.InstanceId, out _),
                "successful marker repair permits one frozen party reward, exit, and empty-instance retirement");
            await registry.AdvanceMonsterWorldOnceAsync(due.AddSeconds(2), CancellationToken.None);
            Check.True(daily.AdmissionAttempts.Count == 8 && rewards.Requests.Count == 1,
                "retired completion does not repeat admission repair or reward settlement");
        }
        finally
        {
            registry.UnregisterAuthoritativeInstanceTransitionSink(leader.Session);
        }
    }
}
