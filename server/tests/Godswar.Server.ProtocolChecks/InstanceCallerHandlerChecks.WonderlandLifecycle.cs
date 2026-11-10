using System.Reflection;
using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Game;
using Godswar.Server.Game.WorldInstances;
using Godswar.Server.Networking;
using Godswar.Server.Protocol;

namespace Godswar.Server.ProtocolChecks;

internal static partial class InstanceCallerHandlerChecks
{
    public const string WonderlandLifecycleCheckName =
        "Wonderland leader termination and committed departures clear UI and retire exact runtimes";

    public static async Task RunWonderlandLifecycleAsync()
    {
        await CheckWonderlandLeaderTerminationAsync();
        await CheckWonderlandAbandonedDepartureAsync();
        await CheckWonderlandTimeoutAsync();
        await CheckWonderlandEarnedTitleSurvivesTerminationAsync();
    }

    private static async Task CheckWonderlandLeaderTerminationAsync()
    {
        await using var fixture = await CreateWonderlandHandlerFixtureAsync(partySize: 2);
        var party = fixture.Party;
        var leader = party.Leader;
        var follower = party.Followers.Single();
        var registry = leader.Registry;
        var runtime = await EnterWonderlandHandlerAsync(fixture);
        var beforeInvalid = AtlantisPacketCounts(party);
        await InvokeAsync(follower.Handler, CreateRepetitionLeave(227, 0));
        await InvokeAsync(leader.Handler, CreateRepetitionLeave(224, 0));
        await InvokeAsync(leader.Handler, CreateRepetitionLeave(227, 1));
        Check.True(registry.ChangePartyLeader(leader.Session, leader.Character.Name,
            follower.Character.Name).Status == PartyOperationStatus.Applied, "current party leader changes");
        Check.True(!registry.TryTerminateWonderlandRun(leader.Session, 227, 0, DateTimeOffset.UtcNow) &&
            !registry.TryTerminateWonderlandRun(follower.Session, 227, 0, DateTimeOffset.UtcNow),
            "termination requires both the original admitted leader and current party leadership");
        Check.True(registry.ChangePartyLeader(follower.Session, follower.Character.Name,
            leader.Character.Name).Status == PartyOperationStatus.Applied, "current party leader is restored");
        await using (var replacement = new ClientSession(new FactionCrierCaptureTransport()))
        {
            registry.ReplaceAccountSession(leader.Character.AccountId, replacement);
            Check.True(!registry.TryTerminateWonderlandRun(leader.Session, 227, 0, DateTimeOffset.UtcNow) &&
                !registry.TryTerminateWonderlandRun(replacement, 227, 0, DateTimeOffset.UtcNow),
                "replaced ownership and an unadmitted replacement cannot terminate the old run");
            GameHandlerOwnershipTestFences.Bind(registry, leader.Session, leader.Character.AccountId, leader.Character);
        }
        Check.True(runtime.Map.TryGetWonderlandSnapshot(out var active) && active.State == WonderlandRunState.Active &&
            party.ReadAllPackets().Select((packets, index) => packets.Skip(beforeInvalid[index])
                .All(packet => !IsAtlantisDepartureClear(packet))).All(value => value),
            "invalid termination actions neither cancel the run nor clear a client's panel");
        var attempts = 0;
        registry.UnregisterAuthoritativeInstanceTransitionSink(follower.Session);
        registry.RegisterAuthoritativeInstanceTransitionSink(follower.Session, (command, token) => ++attempts == 1
            ? Task.FromResult(false) : InvokeAuthoritativeTransitionAsync(follower.Handler, command, token));
        var publishDelayedActive = CaptureWonderlandUiDelivery(registry, runtime);
        var beforeCancel = AtlantisPacketCounts(party);
        await InvokeAsync(leader.Handler, CreateRepetitionLeave(227, 0));
        Check.True(runtime.Map.TryGetWonderlandSnapshot(out var cancelled) && cancelled.State == WonderlandRunState.Cancelled &&
            !runtime.Map.TrySpawnPendingWonderlandStage(DateTimeOffset.UtcNow, out _) &&
            !runtime.Map.TryApplyMonsterDamage(runtime.Map.SnapshotMonsters().First().ObjectId, 1, DateTimeOffset.UtcNow, out _),
            "native leader termination immediately freezes the encounter, spawn publication, and damage");
        var afterReset = AtlantisPacketCounts(party);
        await publishDelayedActive();
        Check.True(runtime.Map.Population == 2 && party.ReadAllPackets()
            .Select((packets, index) => packets.Skip(afterReset[index]).All(packet => ReadOpcode(packet) is not
                (Opcodes.RepetitionSync or Opcodes.RepetitionFightInfo))).All(value => value),
            "a previously captured Active update cannot reopen the leader panel after Reset before party egress");
        await registry.AdvanceMonsterWorldOnceAsync(WonderlandNow(runtime), CancellationToken.None);
        Check.True(attempts == 1 && runtime.Map.Population == 1 && follower.Character.CurrentMap == 207 &&
            registry.TryGetWorldInstance(runtime.InstanceId, out _) &&
            party.ReadAllPackets().All(packets => !packets.Any(IsWonderlandCenteredNotice)),
            "failed member exit preserves the cancelled exact instance for retry and blocks its announcement");
        await registry.AdvanceMonsterWorldOnceAsync(WonderlandNow(runtime), CancellationToken.None);
        Check.True(attempts == 2 && runtime.Map.Population == 0 && !registry.TryGetWorldInstance(runtime.InstanceId, out _) &&
            fixture.Titles.Requests.Count == 0 && party.Characters.All(character => character.MedusaHonorPoints == 1234),
            "the next tick exits the remaining member and retires cancellation without rewards");
        await CompleteAtlantisSceneReadinessAsync(leader.Handler);
        await registry.AdvanceMonsterWorldOnceAsync(WonderlandNow(runtime), CancellationToken.None);
        Check.True(party.ReadAllPackets().All(packets => !packets.Any(IsWonderlandCenteredNotice)),
            "retirement and leader readiness still wait for the final member to load the capital");
        await CompleteAtlantisSceneReadinessAsync(follower.Handler);
        await registry.AdvanceMonsterWorldOnceAsync(WonderlandNow(runtime), CancellationToken.None);
        await registry.AdvanceMonsterWorldOnceAsync(WonderlandNow(runtime), CancellationToken.None);
        await FlushWonderlandNoticeAudienceAsync(party);
        foreach (var (packets, index) in party.ReadAllPackets().Select((packets, index) => (packets, index)))
        {
            var emitted = packets.Skip(beforeCancel[index]).ToArray();
            Check.Equal(1, emitted.Count(IsWonderlandCenteredNotice),
                "a retried party exit never repeats the terminal announcer message");
            var lastReset = Array.FindLastIndex(emitted, IsAtlantisDepartureClear);
            Check.True(lastReset >= 0 && emitted.Count(packet => ReadOpcode(packet) == Opcodes.SceneChange) == 1 &&
                emitted.Skip(lastReset + 1).All(packet => ReadOpcode(packet) is not
                    (Opcodes.RepetitionSync or Opcodes.RepetitionFightInfo)),
                "each committed party departure clears the native UI with no subsequent panel resurrection");
        }
    }

    private static Func<Task> CaptureWonderlandUiDelivery(GameSessionRegistry registry, WorldInstanceRuntime runtime)
    {
        // Capture a genuinely newer timer value, so the old stamp cache alone
        // cannot hide the missing authoritative terminal-state fence.
        var now = WonderlandNow(runtime).AddSeconds(2);
        var captured = runtime.Map.AdvanceWonderland(now);
        var admissions = typeof(GameSessionRegistry).GetField("_wonderlandAdmissions",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(registry)!;
        var admission = admissions.GetType().GetProperty("Item")!.GetValue(admissions, [runtime.InstanceId])!;
        var members = typeof(GameSessionRegistry).GetMethod("SnapshotWonderlandMembersLocked",
            BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(registry, [runtime])!;
        var publish = typeof(GameSessionRegistry).GetMethod("PublishWonderlandRunUiAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        return () => (Task)(publish.Invoke(registry,
            [runtime, captured, admission, members, now, CancellationToken.None]) ??
            throw new InvalidOperationException("Expected the captured Wonderland UI delivery task."));
    }

    private static async Task CheckWonderlandAbandonedDepartureAsync()
    {
        await using var fixture = await CreateWonderlandHandlerFixtureAsync(partySize: 2);
        var leader = fixture.Party.Leader;
        var follower = fixture.Party.Followers.Single();
        var registry = leader.Registry;
        var runtime = await EnterWonderlandHandlerAsync(fixture);
        var before = leader.ReadPackets().Count;
        Check.True(registry.TryTransferMap(leader.Session, 207, 0, 136, -150),
            "ordinary capital travel commits departure from the exact Wonderland instance");
        await FlushAtlantisDepartureAsync(leader.Session);
        Check.True(leader.ReadPackets().Skip(before).Count(IsAtlantisDepartureClear) == 1,
            "ordinary departure clears the native Wonderland panel exactly once");
        await registry.AdvanceMonsterWorldOnceAsync(WonderlandNow(runtime), CancellationToken.None);
        Check.True(runtime.Map.Population == 1 && runtime.Map.TryGetWonderlandSnapshot(out var active) &&
            active.State == WonderlandRunState.Active && registry.TryGetWorldInstance(runtime.InstanceId, out _),
            "one departing member cannot cancel a run that still has an admitted participant");
        registry.Remove(follower.Session);
        await registry.AdvanceMonsterWorldOnceAsync(WonderlandNow(runtime), CancellationToken.None);
        Check.True(runtime.Map.Population == 0 && runtime.Map.TryGetWonderlandSnapshot(out var cancelled) &&
            cancelled.State == WonderlandRunState.Cancelled && !registry.TryGetWorldInstance(runtime.InstanceId, out _) &&
            !leader.ReadPackets().Skip(before).Any(packet => ReadOpcode(packet) is
                Opcodes.RepetitionSync or Opcodes.RepetitionFightInfo),
            "connection cleanup of the final participant cancels and retires the abandoned run without stale UI");
    }

    private static async Task CheckWonderlandTimeoutAsync()
    {
        await using var fixture = await CreateWonderlandHandlerFixtureAsync();
        var leader = fixture.Party.Leader;
        var runtime = await EnterWonderlandHandlerAsync(fixture);
        runtime.Map.TryGetWonderlandSnapshot(out var initial);
        await leader.Registry.AdvanceMonsterWorldOnceAsync(initial.Deadline, CancellationToken.None);
        Check.True(runtime.Map.TryGetWonderlandSnapshot(out var expired) && expired.State == WonderlandRunState.TimedOut &&
            expired.TerminalAt == initial.Deadline && leader.Character.CurrentMap != 207 && runtime.Map.Population == 0 &&
            !leader.Registry.TryGetWorldInstance(runtime.InstanceId, out _) && fixture.Titles.Requests.Count == 0,
            "the exact forty-minute deadline exits and retires an unfinished run without a completion title");
    }

    private static async Task CheckWonderlandEarnedTitleSurvivesTerminationAsync()
    {
        await using var fixture = await CreateWonderlandHandlerFixtureAsync();
        var leader = fixture.Party.Leader;
        var runtime = await EnterWonderlandHandlerAsync(fixture);
        await ClearWonderlandHandlerIslandAsync(fixture, runtime);
        var title = WonderlandTitlePolicy.Resolve(1).TitleId;
        Check.True(fixture.Titles.Requests.Single().IslandNumber == 1 && leader.Character.OwnedTitleIds.Contains(title),
            "island one earns its title before the leader chooses to terminate the remaining run");
        await InvokeAsync(leader.Handler, CreateRepetitionLeave(227, 0));
        await leader.Registry.AdvanceMonsterWorldOnceAsync(WonderlandNow(runtime), CancellationToken.None);
        Check.True(!leader.Registry.TryGetWorldInstance(runtime.InstanceId, out _) && leader.Character.CurrentMap != 207 &&
            fixture.Titles.Requests.Count == 1 && leader.Character.OwnedTitleIds.Contains(title) &&
            leader.Character.SelectedTitleId == 0 && leader.Character.MedusaHonorPoints == 1234,
            "termination retains the already earned first-island title without granting uncleared islands or auto-equipping");
    }
}
