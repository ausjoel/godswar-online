using System.Text;
using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Game;
using Godswar.Server.Game.WorldInstances;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class InstanceCallerHandlerChecks
{
    public const string AtlantisCompletionRewardsCheckName =
        "Atlantis frozen completion entitlement, durable retry, projection, and exit gate";

    public static async Task RunAtlantisCompletionRewardsAsync()
    {
        await CheckAtlantisRewardAcknowledgementRetryAsync();
        await CheckAtlantisOwnedTitlePreservesSelectionAsync();
        await CheckAtlantisOriginalPartyRewardAsync();
        await CheckAtlantisEarnedRewardSurvivesDisconnectAsync();
        await CheckAtlantisRewardFailureHoldsExitAsync();
        await CheckAtlantisZeroEligibleCompletionAsync();
        await CheckAtlantisCompletionRepairsAdmissionMarkersAsync();
    }

    private static async Task CheckAtlantisRewardAcknowledgementRetryAsync()
    {
        await using var fixture = await CreateAtlantisOpalFixtureAsync(null, null, partySize: 1);
        var registry = fixture.Leader.Registry;
        var store = new ScriptedAtlantisCompletionRewards(fixture.Characters)
        {
            LoseFirstCommitAcknowledgement = true
        };
        registry.ConfigureAtlantisCompletionRewards(store);
        var (runtime, initial) = await PrepareAtlantisDepartureAsync(fixture);
        var before = fixture.Leader.Character.MedusaHonorPoints;
        var revisionBefore = fixture.Leader.Character.MedusaRewardRevision;
        var completed = CompleteAtlantisRewardRun(runtime, initial);
        var at = completed.TerminalAt!.Value;
        await registry.AdvanceMonsterWorldOnceAsync(at.AddSeconds(1), CancellationToken.None);
        Check.True(store.CommitCount == 1 && store.Requests.Count == 1 &&
            fixture.Leader.Character.MedusaHonorPoints == before && runtime.Map.Population == 1,
            "a lost durable acknowledgement does not project or exit before settlement is confirmed");
        await registry.AdvanceMonsterWorldOnceAsync(at.AddSeconds(2), CancellationToken.None);
        Check.True(store.CommitCount == 1 && store.Requests.Count == 2 &&
            store.Requests.Select(request => request.RequestHash).Distinct().Count() == 1,
            "retry uses the same frozen completion request and receives the existing durable receipt");
        var character = fixture.Leader.Character;
        Check.True(character.MedusaHonorPoints == before + 2800 &&
            character.MedusaRewardRevision == revisionBefore + 1 &&
            character.OwnedTitleIds.Contains(AtlantisCompletionRewardPolicy.DeepSeaHunterTitleId),
            "confirmed solo completion projects2800 HardPoints and Deep Sea Hunter exactly once");
        var after = AtlantisPacketCounts(fixture);
        Check.Equal(1, fixture.Leader.ReadPackets().Count(IsAtlantisCompletionAnnouncement),
            "a recovered durable receipt announces solo completion once");
        await registry.AdvanceMonsterWorldOnceAsync(at.AddSeconds(3), CancellationToken.None);
        await registry.AdvanceMonsterWorldOnceAsync(at.AddSeconds(4), CancellationToken.None);
        Check.True(store.Requests.Count == 2 && character.MedusaHonorPoints == before + 2800 &&
            fixture.Leader.ReadPackets().Skip(after[0]).All(packet =>
                ReadOpcode(packet) != Opcodes.RepetitionReward && !IsAtlantisCompletionAnnouncement(packet)),
            "settlement cache avoids another store call or reward dispatch on later completion ticks");
        Check.Equal(AtlantisCompletionRewardPolicy.DeepSeaHunterTitleId, store.Requests[0].Award.TitleId,
            "the original solo admission selects its solo completion title");
    }

    private static async Task CheckAtlantisOriginalPartyRewardAsync()
    {
        await using var fixture = await CreateAtlantisOpalFixtureAsync(null, null, partySize: 2);
        var registry = fixture.Leader.Registry;
        var store = new ScriptedAtlantisCompletionRewards(fixture.Characters);
        registry.ConfigureAtlantisCompletionRewards(store);
        var (runtime, initial) = await PrepareAtlantisDepartureAsync(fixture);
        var follower = fixture.Followers.Single();
        var followerBefore = AtlantisRewardState(follower.Character);
        follower.Character.Camp = fixture.Leader.Character.Camp == GameDefaults.SpartaCamp
            ? GameDefaults.AthensCamp : GameDefaults.SpartaCamp;
        Check.True(registry.TryTransferMap(follower.Session, 205, 0, 136f, -150f),
            "one originally admitted party member leaves before the final scoring kill");
        Check.True(registry.TryMarkWorldReady(follower.Session, new Dictionary<uint, long>(),
                out _, initial.StartedAt.AddSeconds(1)),
            "the outside-instance observer finishes destination hydration before the completion notice");
        var completed = CompleteAtlantisRewardRun(runtime, initial);
        var evidence = runtime.Map.GetAtlantisCompletionEvidence()!;
        var request = evidence.Request!;
        Check.True(request.AdmittedCharacterIds.Count == 2 &&
            request.CharacterIds.SequenceEqual(new[] { fixture.Leader.Character.Id }) &&
            request.Award.TitleId == AtlantisCompletionRewardPolicy.SeabedExplorerTitleId,
            "point850 freezes the remaining eligible member while retaining the original party classification");
        await registry.AdvanceMonsterWorldOnceAsync(completed.TerminalAt!.Value.AddSeconds(1), CancellationToken.None);
        Check.True(store.Requests.Count == 1 && store.Committed(runtime.InstanceId).Members.Count == 1 &&
            fixture.Leader.Character.OwnedTitleIds.Contains(AtlantisCompletionRewardPolicy.SeabedExplorerTitleId) &&
            AtlantisRewardState(follower.Character) == followerBefore,
            "only the eligible finisher receives the party title and reward; an early departure receives neither");
        Check.True(fixture.Leader.ReadPackets().Count(IsAtlantisCompletionAnnouncement) == 1 &&
            follower.Transport.ReadLegacyPackets().Count(IsAtlantisCompletionAnnouncement) == 1,
            "completion announces realm-wide across both camps, including an outside-instance observer");
    }

    private static async Task CheckAtlantisEarnedRewardSurvivesDisconnectAsync()
    {
        await using var fixture = await CreateAtlantisOpalFixtureAsync(null, null, partySize: 2);
        var registry = fixture.Leader.Registry;
        var store = new ScriptedAtlantisCompletionRewards(fixture.Characters);
        registry.ConfigureAtlantisCompletionRewards(store);
        var (runtime, initial) = await PrepareAtlantisDepartureAsync(fixture);
        var completed = CompleteAtlantisRewardRun(runtime, initial);
        var frozen = runtime.Map.GetAtlantisCompletionEvidence()!;
        var follower = fixture.Followers.Single();
        var before = follower.Character.MedusaHonorPoints;
        follower.Session.Disconnect();
        registry.Remove(follower.Session);
        await registry.AdvanceMonsterWorldOnceAsync(completed.TerminalAt!.Value.AddSeconds(1), CancellationToken.None);
        var receipt = store.Committed(runtime.InstanceId);
        Check.True(frozen.Request!.CharacterIds.Count == 2 &&
            receipt.Members.Any(member => member.CharacterId == follower.Character.Id &&
                member.HonorAfter == before + 2800) &&
            ReferenceEquals(frozen, runtime.Map.GetAtlantisCompletionEvidence()),
            "disconnect after point850 cannot remove an already frozen durable reward recipient");
        Check.Equal(before, follower.Character.MedusaHonorPoints,
            "disconnected character memory is not used as a live reward projection target");
        Check.True(fixture.Leader.Character.OwnedTitleIds.Contains(AtlantisCompletionRewardPolicy.SeabedExplorerTitleId),
            "the remaining current member receives the same original-party completion title");
    }

    private static async Task CheckAtlantisRewardFailureHoldsExitAsync()
    {
        await using var fixture = await CreateAtlantisOpalFixtureAsync(null, null, partySize: 1);
        var leader = fixture.Leader;
        var registry = leader.Registry;
        var store = new ScriptedAtlantisCompletionRewards(fixture.Characters) { RejectAttemptsRemaining = 2 };
        registry.ConfigureAtlantisCompletionRewards(store);
        registry.RegisterAuthoritativeInstanceTransitionSink(leader.Session,
            (command, token) => InvokeAuthoritativeTransitionAsync(leader.Handler, command, token));
        try
        {
            var (runtime, initial) = await PrepareAtlantisDepartureAsync(fixture);
            var completed = CompleteAtlantisRewardRun(runtime, initial);
            var due = completed.TerminalAt!.Value.AddSeconds(30);
            await registry.AdvanceMonsterWorldOnceAsync(due, CancellationToken.None);
            await registry.AdvanceMonsterWorldOnceAsync(due.AddSeconds(1), CancellationToken.None);
            Check.True(store.CommitCount == 0 && runtime.Map.Population == 1 &&
                leader.Character.CurrentMap == 205 && registry.TryGetWorldInstance(runtime.InstanceId, out _),
                "an expired countdown and repeated reward rejection preserve the member and source runtime");
            await registry.AdvanceMonsterWorldOnceAsync(due.AddSeconds(2), CancellationToken.None);
            Check.True(store.CommitCount == 1 && store.Requests.Count == 3 &&
                leader.Character.CurrentMap != 205 && runtime.Map.Population == 0 &&
                !registry.TryGetWorldInstance(runtime.InstanceId, out _),
                "the successful retry rewards, exits, and retires without restarting the thirty-second delay");
        }
        finally
        {
            registry.UnregisterAuthoritativeInstanceTransitionSink(leader.Session);
        }
    }

    private static async Task CheckAtlantisZeroEligibleCompletionAsync()
    {
        await using var fixture = await CreateAtlantisOpalFixtureAsync(null, null, partySize: 1);
        var registry = fixture.Leader.Registry;
        var store = new ScriptedAtlantisCompletionRewards(fixture.Characters);
        registry.ConfigureAtlantisCompletionRewards(store);
        var (runtime, initial) = await PrepareAtlantisDepartureAsync(fixture);
        var before = AtlantisRewardState(fixture.Leader.Character);
        var completed = CompleteAtlantisRewardRun(runtime, initial, beforeFinalSettlement: () =>
        {
            Check.True(registry.TryTransferMap(fixture.Leader.Session, 205, 0, 136f, -150f),
                "the final recipient departs after boss death but before its delayed scoring settlement");
        });
        Check.True(runtime.Map.GetAtlantisCompletionEvidence() is { Request: null, Members.Count: 0 },
            "point850 with no eligible members freezes an explicit empty entitlement");
        await registry.AdvanceMonsterWorldOnceAsync(completed.TerminalAt!.Value.AddSeconds(1), CancellationToken.None);
        Check.True(store.Requests.Count == 0 && store.CommitCount == 0 &&
            AtlantisRewardState(fixture.Leader.Character) == before &&
            !fixture.Leader.ReadPackets().Any(IsAtlantisCompletionAnnouncement) &&
            !registry.TryGetWorldInstance(runtime.InstanceId, out _),
            "empty completion creates no award or announcement and safely releases the finished instance");
    }

    private static bool IsAtlantisCompletionAnnouncement(byte[] packet) =>
        ReadOpcode(packet) == Opcodes.PythonNote &&
        Encoding.ASCII.GetString(packet).Contains("cleared Atlantis", StringComparison.Ordinal);

    private static AtlantisRunSnapshot CompleteAtlantisRewardRun(
        WorldInstanceRuntime runtime, AtlantisRunSnapshot initial, Action? beforeFinalSettlement = null)
    {
        AtlantisRunSnapshot? completed = null;
        for (var wave = 0; wave < 25; wave++)
        {
            var at = initial.StartedAt.AddSeconds(wave + 1);
            _ = runtime.Map.TrySpawnPendingAtlantisWave(at, out _);
            var monsters = runtime.Map.SnapshotMonsters().Where(monster =>
                monster.IsAlive && IsAtlantisScoringObject(monster.ObjectId)).ToArray();
            Check.Equal(wave % 5 == 4 ? 1 : 12, monsters.Length, "completion fixture exposes the expected scored wave");
            foreach (var monster in monsters)
            {
                Check.True(runtime.Map.TryApplyMonsterDamage(monster.ObjectId, monster.CurrentHealth,
                    at, out var death) && death.Killed, "completion fixture commits an authoritative monster death");
                if (wave == 24) beforeFinalSettlement?.Invoke();
                completed = AtlantisMonsterKillScoring.RecordCommitted(runtime.Map,
                    GameplayContentTestFixtures.Published, runtime.InstanceId, death, at).Snapshot;
            }
        }
        Check.True(completed is { State: AtlantisRunState.Completed, TeamPoints: 850 } &&
            runtime.Map.GetAtlantisCompletionEvidence() is not null,
            "the final committed boss kill freezes durable completion evidence at850 points");
        return completed!;
    }
}
