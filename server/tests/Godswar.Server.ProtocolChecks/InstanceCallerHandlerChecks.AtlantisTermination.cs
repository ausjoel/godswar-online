using System.Reflection;
using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Game;
using Godswar.Server.Game.WorldInstances;
using Godswar.Server.Networking;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class InstanceCallerHandlerChecks
{
    public const string AtlantisTerminationCheckName =
        "Atlantis native termination cancels only the owned leader run and exits the party";

    public static async Task RunAtlantisTerminationAsync()
    {
        CheckAtlantisCancellationState();
        foreach (var partySize in new[] { 1, 5 })
        {
            await CheckAtlantisLeaderTerminationAsync(partySize);
        }
    }

    private static void CheckAtlantisCancellationState()
    {
        var descriptor = AtlantisRunRuntimeChecks.Descriptor();
        var start = descriptor.CreatedAt;
        var run = new AtlantisRunRuntime(descriptor, start);
        run.RecordCommittedMonsterKill(descriptor.InstanceId, 1, 1,
            AtlantisMonsterRank.Normal, start.AddSeconds(2));
        var cancelled = run.Cancel(start.AddSeconds(1));
        Check.True(cancelled.State == AtlantisRunState.Cancelled && cancelled.TeamPoints == 1 &&
            cancelled.TerminalAt == start.AddSeconds(2),
            "cancellation freezes earned points and never moves the observed clock backward");
        Check.Equal(cancelled, run.Cancel(start.AddHours(1)), "repeated cancellation preserves terminal state");
        Check.Equal(cancelled, run.Advance(start.AddHours(1)), "cancelled run never later times out");
        Check.True(run.RecordCommittedMonsterKill(descriptor.InstanceId, 2, 1,
                AtlantisMonsterRank.Boss, start.AddSeconds(3)).PointsAwarded == 0,
            "no committed kill adds points after cancellation");

        var expired = new AtlantisRunRuntime(descriptor, start);
        Check.True(expired.Cancel(start.AddHours(1)).State == AtlantisRunState.TimedOut,
            "late cancellation preserves the authoritative timeout outcome");
        var complete = new AtlantisRunRuntime(descriptor, start);
        for (uint id = 1; id <= 17; id++)
        {
            complete.RecordCommittedMonsterKill(descriptor.InstanceId, id, 1,
                AtlantisMonsterRank.Boss, start.AddSeconds(1));
        }
        Check.True(complete.Cancel(start.AddSeconds(2)).State == AtlantisRunState.Completed,
            "leaving a completed run cannot rewrite its completion result");
    }

    private static async Task CheckAtlantisLeaderTerminationAsync(int partySize)
    {
        var daily = new ScriptedLegacyInstanceDailyEntryStore();
        var payments = new ScriptedLegacyInstanceOpalPaymentStore();
        await using var fixture = await CreateAtlantisOpalFixtureAsync(daily, payments, partySize: partySize);
        var leader = fixture.Leader;
        var registry = leader.Registry;
        // An ended run settles the tier its score reached, so this check needs a
        // real reward store to observe the settlement.
        var rewards = new ScriptedAtlantisCompletionRewards(fixture.Characters);
        registry.ConfigureAtlantisCompletionRewards(rewards);
        registry.RegisterAuthoritativeInstanceTransitionSink(leader.Session,
            (command, token) => InvokeAuthoritativeTransitionAsync(leader.Handler, command, token));
        try
        {
            await EnterAtlantisAsync(fixture, InstanceCallerProtocol.AtlantisEnterSubId);
            await CompleteAtlantisSceneReadinessAsync(leader.Handler);
            foreach (var follower in fixture.Followers)
            {
                await CompleteAtlantisSceneReadinessAsync(follower.Handler);
            }
            var instanceId = GetSourceInstanceId(leader);
            var directory = (LocalWorldInstanceRuntimeDirectory)typeof(GameSessionRegistry)
                .GetProperty("WorldInstances", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(registry)!;
            Check.True(directory.TryFind(instanceId, out var runtime) &&
                registry.TryGetAtlantisEncounterSnapshot(instanceId, out _),
                "termination fixture enters a real exact Atlantis dungeon");
            registry.TryGetAtlantisEncounterSnapshot(instanceId, out var initial);
            await registry.AdvanceMonsterWorldOnceAsync(initial.StartedAt, CancellationToken.None);

            var monster = runtime.Map.SnapshotMonsters().First();
            Check.True(runtime.Map.TryApplyMonsterDamage(monster.ObjectId, monster.CurrentHealth,
                    DateTimeOffset.UtcNow, out var death) && death.Killed,
                "termination fixture has one committed monster death before cancellation");
            var committed = registry.CaptureAtlantisMonsterKill(leader.Session, death);
            Check.True(committed is not null, "termination fixture captures its owned committed score");
            committed!();
            registry.TryGetAtlantisEncounterSnapshot(instanceId, out var before);
            Check.Equal(1, before.TeamPoints, "normal monster contributed exactly one point before cancellation");

            var beforeInvalid = AtlantisPacketCounts(fixture);
            await InvokeAsync(leader.Handler, CreateRepetitionPanelAction(1, 0));
            await InvokeAsync(leader.Handler, CreateRepetitionLeave(200, 0));
            await InvokeAsync(leader.Handler, CreateRepetitionLeave(224, 1));
            if (fixture.Followers.FirstOrDefault() is { } member)
            {
                await InvokeAsync(member.Handler, CreateRepetitionPanelAction(0, 0));
                Check.True(registry.ChangePartyLeader(leader.Session, leader.Character.Name,
                        member.Character.Name).Status == PartyOperationStatus.Applied,
                    "fixture changes current party leadership");
                // Instance leadership alone gates the end control. Party
                // leadership is a separate system: a party leader who is not the
                // instance leader is still refused, and the instance leader's own
                // control no longer depends on holding party leadership. The
                // instance leader's legitimate termination is asserted below.
                Check.True(!registry.TryTerminateAtlantisRunFromLeader(member.Session, DateTimeOffset.UtcNow),
                    "a party leader who is not the instance leader cannot terminate Atlantis");
                Check.True(registry.ChangePartyLeader(member.Session, member.Character.Name,
                        leader.Character.Name).Status == PartyOperationStatus.Applied,
                    "fixture restores current party leadership");
            }
            await using (var replacement = new ClientSession(new FactionCrierCaptureTransport()))
            {
                registry.ReplaceAccountSession(leader.Character.AccountId, replacement);
                Check.True(!registry.TryTerminateAtlantisRunFromLeader(leader.Session, DateTimeOffset.UtcNow) &&
                    !registry.TryTerminateAtlantisRunFromLeader(replacement, DateTimeOffset.UtcNow),
                    "neither replaced claimant ownership nor an unadmitted replacement can terminate Atlantis");
                GameHandlerOwnershipTestFences.Bind(registry, leader.Session,
                    leader.Character.AccountId, leader.Character);
            }
            registry.TryGetAtlantisEncounterSnapshot(instanceId, out var afterInvalid);
            Check.Equal(before, afterInvalid, "spoofed, follower, and stale-owner actions leave the run unchanged");
            Check.True(fixture.ReadAllPackets().Select((packets, index) => packets.Skip(beforeInvalid[index])
                    .All(packet => ReadOpcode(packet) != Opcodes.RepetitionReset)).All(static ok => ok),
                "rejected controls never acknowledge a successful termination");

            var failedExitAttempts = 0;
            if (fixture.Followers.FirstOrDefault() is { } retryMember)
            {
                registry.UnregisterAuthoritativeInstanceTransitionSink(retryMember.Session);
                registry.RegisterAuthoritativeInstanceTransitionSink(retryMember.Session,
                    (command, token) => ++failedExitAttempts == 1
                        ? Task.FromResult(false)
                        : InvokeAuthoritativeTransitionAsync(retryMember.Handler, command, token));
            }
            var rewardsBefore = fixture.Characters.Select(AtlantisRewardState).ToArray();
            var delayedActiveDelivery = CaptureAtlantisDepartureDelivery(
                registry, runtime, DateTimeOffset.UtcNow);
            var beforeExit = AtlantisPacketCounts(fixture);
            await InvokeAsync(leader.Handler, partySize == 1
                ? CreateRepetitionPanelAction(0, 0xED)
                : CreateRepetitionLeave(224, 0));
            Check.True(runtime.Map.TryGetAtlantisRunSnapshot(out var cancelled) &&
                cancelled.State == AtlantisRunState.Cancelled && cancelled.TeamPoints == 1 &&
                runtime.Map.TryGetAtlantisWaveSnapshot(out var wave) && wave.State == AtlantisWaveState.Cancelled,
                "both native controls synchronously cancel the score and live wave authorities");
            var afterReset = AtlantisPacketCounts(fixture);
            await PublishAtlantisDepartureDeliveryAsync(registry, delayedActiveDelivery);
            foreach (var session in fixture.Sessions)
            {
                await FlushAtlantisDepartureAsync(session);
            }
            Check.True(fixture.Characters.All(static character => character.CurrentMap == 205) &&
                fixture.ReadAllPackets().Select((packets, index) => packets.Skip(afterReset[index])
                    .All(packet => ReadOpcode(packet) is not
                        (Opcodes.RepetitionSync or Opcodes.RepetitionFightInfo))).All(static ok => ok),
                "a captured Active delivery cannot reopen the panel after Terminate while the party still awaits egress");
            var living = runtime.Map.SnapshotMonsters().First(static value => value.IsAlive);
            Check.True(!runtime.Map.TryApplyMonsterDamage(living.ObjectId, 1, DateTimeOffset.UtcNow, out _) &&
                !runtime.Map.TrySpawnPendingAtlantisWave(DateTimeOffset.UtcNow, out _),
                "cancelled Atlantis stops combat and cannot publish another wave");
            // The ended run behaves exactly like a completed one: the panel
            // becomes the native leave countdown and nobody is moved yet.
            await registry.AdvanceMonsterWorldOnceAsync(DateTimeOffset.UtcNow, CancellationToken.None);
            Check.True(
                fixture.Characters.All(static character => character.CurrentMap == 205) &&
                runtime.Map.Population == fixture.Characters.Count,
                "the end countdown keeps every member inside until it expires");
            foreach (var (packets, index) in fixture.ReadAllPackets().Select((packets, index) => (packets, index)))
            {
                var shown = packets.Skip(afterReset[index]).TakeWhile(packet =>
                    ReadOpcode(packet) != Opcodes.SceneChange).ToArray();
                Check.True(
                    shown.Any(packet =>
                        packet.SequenceEqual(PacketBuilder.RepetitionPanelCompletion())) &&
                    shown.Any(packet => ReadOpcode(packet) == Opcodes.RepetitionCompletionState &&
                        System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(packet.AsSpan(8)) == 1) &&
                    // Native 10231 carries the leave countdown when its seconds
                    // field is non-zero; zero is the plain teardown.
                    shown.Any(packet => ReadOpcode(packet) == Opcodes.RepetitionReset &&
                        System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(packet.AsSpan(4)) == 30) &&
                    // The leader who pressed Terminate gets the same countdown as
                    // his party: a zero-second teardown in this batch used to wipe
                    // his own panel and left him unable to leave.
                    !shown.Any(packet => packet.SequenceEqual(PacketBuilder.RepetitionReset())),
                    "the ended run publishes the native leave countdown to every member, " +
                    "including the leader who ended it, and wipes nobody's panel");
            }

            // Once the countdown expires the ordinary egress carries them home,
            // and a member whose exact write deferred is retried by a later tick.
            var afterWindow = DateTimeOffset.UtcNow.AddSeconds(31);
            await registry.AdvanceMonsterWorldOnceAsync(afterWindow, CancellationToken.None);
            if (partySize > 1)
            {
                Check.True(failedExitAttempts == 1 && runtime.Map.Population == 1 &&
                    registry.TryGetWorldInstance(instanceId, out _),
                    "failed member egress retains the cancelled run and its retry request");
                await registry.AdvanceMonsterWorldOnceAsync(afterWindow, CancellationToken.None);
                Check.Equal(2, failedExitAttempts, "next world tick retries only the untransferred member");
            }
            Check.True(fixture.Characters.All(character => character.CurrentMap ==
                    (character.Camp == GameDefaults.SpartaCamp
                        ? GameDefaults.SpartaCapitalMap : GameDefaults.AthensCapitalMap)) &&
                runtime.Map.Population == 0,
                "termination exits every admitted online member to their established camp capital");
            foreach (var (packets, index) in fixture.ReadAllPackets().Select((packets, index) => (packets, index)))
            {
                var emitted = packets.Skip(beforeExit[index]).ToArray();
                Check.True(
                    emitted.Any(packet => ReadOpcode(packet) == Opcodes.RepetitionReset &&
                        System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(packet.AsSpan(4)) == 30) &&
                    emitted.Any(packet => ReadOpcode(packet) == Opcodes.SceneChange) &&
                    emitted.Any(packet => packet.SequenceEqual(PacketBuilder.RepetitionReset())) &&
                    // The run ended at one point, which is the documented zero tier:
                    // the members are paid that tier and no title.
                    // Native repetition reward carries the HardPoint award at +92.
                    emitted.Count(packet =>
                        ReadOpcode(packet) == Opcodes.RepetitionReward &&
                        System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(packet.AsSpan(92)) == 200) == 1,
                    "every transferred client sees the leave countdown, changes scene, is paid its " +
                    "ended-run tier once and clears its native panel");
            }
            // The settlement ran once, at the run's own final score, and paid the
            // published incomplete tier without granting a title.
            Check.True(rewards.Requests.Count == 1 &&
                rewards.Requests[0].FinalScore == 1 &&
                rewards.Requests[0].Award.HardPoints == 200 &&
                rewards.Requests[0].Award.TitleId == 0,
                "an ended Atlantis run settles exactly once at its actual score with no title");
            Check.True(
                fixture.Characters.All(character =>
                    character.MedusaHonorPoints == 200 && character.MedusaRewardRevision == 1) &&
                fixture.Characters.All(character => !character.OwnedTitleIds.Contains(
                    AtlantisCompletionRewardPolicy.DeepSeaHunterTitleId) &&
                    !character.OwnedTitleIds.Contains(
                        AtlantisCompletionRewardPolicy.SeabedExplorerTitleId)) &&
                daily.Claims.Count == 1 && payments.Charges.Count == 0,
                "the ended run pays no completion title and neither refunds nor charges " +
                "another instance admission");
            await registry.AdvanceMonsterWorldOnceAsync(DateTimeOffset.UtcNow, CancellationToken.None);
            Check.True(!registry.TryGetWorldInstance(instanceId, out _) &&
                !registry.TryTerminateAtlantisRunFromLeader(leader.Session, DateTimeOffset.UtcNow),
                "empty cancelled instance retires and replayed controls cannot target it from outside");
            foreach (var field in new[] { "_instanceRunLeaders", "_atlantisTerminationExitRequested",
                "_atlantisTerminationEgressInFlight" })
            {
                Check.Equal(0, AtlantisRegistryCacheCount(registry, field), "retirement clears " + field);
            }
        }
        finally
        {
            registry.UnregisterAuthoritativeInstanceTransitionSink(leader.Session);
        }
    }
}
