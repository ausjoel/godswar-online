using System.Reflection;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Game;
using Godswar.Server.Game.WorldInstances;
using Godswar.Server.Networking;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;

namespace Godswar.Server.ProtocolChecks;

internal static partial class InstanceCallerHandlerChecks
{
    public const string AtlantisDepartureCheckName =
        "Atlantis committed departures clear UI and retire abandoned active runs";

    public static async Task RunAtlantisDepartureAsync()
    {
        await CheckAtlantisSoloDepartureAsync();
        await CheckAtlantisDepartureBeforeFirstUiTickAsync();
        await CheckAtlantisPartialPartyDepartureAsync();
        await CheckAtlantisDisconnectedDepartureAsync();
        await CheckAtlantisFailedDepartureAsync();
        await CheckAtlantisActiveEmptyAdmissionRetainedAsync();
    }

    private static async Task CheckAtlantisSoloDepartureAsync()
    {
        await using var fixture = await CreateAtlantisOpalFixtureAsync(null, null, partySize: 1);
        var (runtime, run) = await PrepareAtlantisDepartureAsync(fixture);
        var leader = fixture.Leader;
        var delayed = CaptureAtlantisDepartureDelivery(leader.Registry, runtime, run.StartedAt.AddSeconds(1));
        var before = leader.ReadPackets().Count;

        Check.True(leader.Registry.TryTransferMap(leader.Session, 205, 0, 136f, -150f),
            "the actual registry commits a Sparta departure from active Atlantis");
        await FlushAtlantisDepartureAsync(leader.Session);
        AssertAtlantisDepartureClear(leader.ReadPackets().Skip(before));
        Check.True(leader.Character.CurrentMap == 0 &&
            GetSourceInstanceId(leader) != runtime.InstanceId && runtime.Map.Population == 0,
            "Sparta travel removes the exact Atlantis membership");

        var afterClear = leader.ReadPackets().Count;
        await PublishAtlantisDepartureDeliveryAsync(leader.Registry, delayed);
        await FlushAtlantisDepartureAsync(leader.Session);
        Check.True(!leader.ReadPackets().Skip(afterClear).Any(IsAtlantisPanelPacket),
            "an already captured old-instance delivery cannot reopen the departed player's panel");
        await leader.Registry.AdvanceMonsterWorldOnceAsync(run.StartedAt.AddSeconds(2), CancellationToken.None);
        AssertAtlantisDepartureRetired(leader.Registry, runtime);
        Check.True(!leader.ReadPackets().Skip(afterClear).Any(IsAtlantisPanelPacket),
            "abandoned-run cancellation does not publish Atlantis UI into Sparta");
    }

    private static async Task CheckAtlantisPartialPartyDepartureAsync()
    {
        await using var fixture = await CreateAtlantisOpalFixtureAsync(null, null, partySize: 2);
        var (runtime, run) = await PrepareAtlantisDepartureAsync(fixture);
        var leader = fixture.Leader;
        var follower = fixture.Followers.Single();
        var leaderBefore = leader.ReadPackets().Count;
        var followerBefore = follower.Transport.ReadLegacyPackets().Count;

        Check.True(leader.Registry.TryTransferMap(leader.Session, 205, 0, 136f, -150f),
            "a party leader can leave while a member remains inside Atlantis");
        await FlushAtlantisDepartureAsync(leader.Session);
        AssertAtlantisDepartureClear(leader.ReadPackets().Skip(leaderBefore));
        await leader.Registry.AdvanceMonsterWorldOnceAsync(run.StartedAt.AddSeconds(1), CancellationToken.None);
        Check.True(leader.Registry.TryGetWorldInstance(runtime.InstanceId, out _) &&
            runtime.Map.Population == 1 && follower.Character.CurrentMap == 205 &&
            leader.Registry.TryGetAtlantisEncounterSnapshot(runtime.InstanceId, out var remaining) &&
            remaining.State == AtlantisRunState.Active && remaining.TeamPoints == 0,
            "one member's departure preserves the remaining member's active encounter");
        Check.True(!follower.Transport.ReadLegacyPackets().Skip(followerBefore).Any(IsAtlantisDepartureClear),
            "a departing leader does not clear the remaining member's panel");

        followerBefore = follower.Transport.ReadLegacyPackets().Count;
        Check.True(leader.Registry.TryTransferMap(follower.Session, 205, 0, 136f, -150f),
            "the last party member commits its independent Sparta departure");
        await FlushAtlantisDepartureAsync(follower.Session);
        AssertAtlantisDepartureClear(follower.Transport.ReadLegacyPackets().Skip(followerBefore));
        await leader.Registry.AdvanceMonsterWorldOnceAsync(run.StartedAt.AddSeconds(2), CancellationToken.None);
        AssertAtlantisDepartureRetired(leader.Registry, runtime);
    }

    private static async Task CheckAtlantisDepartureBeforeFirstUiTickAsync()
    {
        await using var fixture = await CreateAtlantisOpalFixtureAsync(null, null, partySize: 1);
        var (runtime, run) = await PrepareAtlantisDepartureAsync(fixture, publishInitialUi: false);
        var leader = fixture.Leader;
        Check.Equal(0, AtlantisRegistryCacheCount(leader.Registry, "_atlantisUi"),
            "departure precedes the first Atlantis UI cache publication");
        var before = leader.ReadPackets().Count;
        Check.True(leader.Registry.TryTransferMap(leader.Session, 205, 0, 136f, -150f),
            "a player can commit departure before the first Atlantis UI tick");
        await FlushAtlantisDepartureAsync(leader.Session);
        AssertAtlantisDepartureClear(leader.ReadPackets().Skip(before));
        Check.True(leader.Character.CurrentMap == 0 && runtime.Map.Population == 0,
            "the early departure removes the exact Atlantis membership");
        await leader.Registry.AdvanceMonsterWorldOnceAsync(run.StartedAt.AddSeconds(1), CancellationToken.None);
        AssertAtlantisDepartureRetired(leader.Registry, runtime);
    }

    private static async Task CheckAtlantisDisconnectedDepartureAsync()
    {
        await using var fixture = await CreateAtlantisOpalFixtureAsync(null, null, partySize: 1);
        var (runtime, run) = await PrepareAtlantisDepartureAsync(fixture);
        var leader = fixture.Leader;
        leader.Session.Disconnect();
        try
        {
            // Ordinary disconnect wakes the connection owner, whose finally
            // removes registry membership. Egress-failure cleanup is a
            // different path and is not scheduled by Disconnect itself.
            await leader.Handler.RunAsync(CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (OperationCanceledException) when (leader.Session.IsDisconnected)
        {
            // The canceled transport read has already unwound handler cleanup.
        }
        Check.Equal(0, runtime.Map.Population,
            "the disconnected connection owner's cleanup removes the final Atlantis member");
        await leader.Registry.AdvanceMonsterWorldOnceAsync(run.StartedAt.AddSeconds(1), CancellationToken.None);
        AssertAtlantisDepartureRetired(leader.Registry, runtime);
    }

    private static async Task CheckAtlantisFailedDepartureAsync()
    {
        await using var fixture = await CreateAtlantisOpalFixtureAsync(null, null, partySize: 1);
        var (runtime, run) = await PrepareAtlantisDepartureAsync(fixture);
        var leader = fixture.Leader;
        var before = leader.ReadPackets().Count;
        var originalAccount = leader.Character.AccountId;
        try
        {
            // Existing ECS transfer checks use an account/character mismatch
            // to reject destination hydration after placement was prepared.
            leader.Character.AccountId = checked(originalAccount + 1);
            Check.Throws<InvalidOperationException>(() =>
            {
                _ = leader.Registry.TryTransferMap(leader.Session, 205, 0, 136f, -150f);
            }, "failed destination hydration rolls back the attempted Atlantis departure");
        }
        finally
        {
            leader.Character.AccountId = originalAccount;
        }
        await FlushAtlantisDepartureAsync(leader.Session);
        Check.True(leader.Character.CurrentMap == 205 &&
            GetSourceInstanceId(leader) == runtime.InstanceId && runtime.Map.Population == 1 &&
            !leader.ReadPackets().Skip(before).Any(IsAtlantisDepartureClear) &&
            AtlantisRegistryCacheCount(leader.Registry, "_atlantisUi") == 1 &&
            AtlantisRegistryCacheCount(leader.Registry, "_atlantisDepartures") == 0,
            "rollback retains exact membership and UI without recording a committed departure");
        await leader.Registry.AdvanceMonsterWorldOnceAsync(run.StartedAt.AddSeconds(1), CancellationToken.None);
        Check.True(leader.Registry.TryGetAtlantisEncounterSnapshot(runtime.InstanceId, out var active) &&
            active.State == AtlantisRunState.Active && active.TeamPoints == 0 &&
            runtime.Map.SnapshotMonsters().Count(monster =>
                monster.IsAlive && IsAtlantisScoringObject(monster.ObjectId)) == 12 &&
            !leader.ReadPackets().Skip(before).Any(IsAtlantisDepartureClear),
            "the next world tick preserves the active wave and panel after a failed transfer");
    }

    private static async Task<(WorldInstanceRuntime Runtime, AtlantisRunSnapshot Run)>
        PrepareAtlantisDepartureAsync(AtlantisOpalFixture fixture, bool publishInitialUi = true)
    {
        await EnterAtlantisAsync(fixture, InstanceCallerProtocol.AtlantisEnterSubId);
        await CompleteAtlantisSceneReadinessAsync(fixture.Leader.Handler);
        foreach (var follower in fixture.Followers)
        {
            await CompleteAtlantisSceneReadinessAsync(follower.Handler);
        }
        var registry = fixture.Leader.Registry;
        var instanceId = GetSourceInstanceId(fixture.Leader);
        var directory = (LocalWorldInstanceRuntimeDirectory)typeof(GameSessionRegistry)
            .GetProperty("WorldInstances", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(registry)!;
        Check.True(directory.TryFind(instanceId, out var runtime) &&
            registry.TryGetAtlantisEncounterSnapshot(instanceId, out _),
            "departure fixture owns an actual Atlantis instance");
        Check.True(registry.TryGetAtlantisEncounterSnapshot(instanceId, out var run),
            "departure fixture has its active Atlantis clock");
        if (publishInitialUi)
        {
            await registry.AdvanceMonsterWorldOnceAsync(run.StartedAt, CancellationToken.None);
            Check.Equal(fixture.Sessions.Count, AtlantisRegistryCacheCount(registry, "_atlantisUi"),
                "each admitted member has a published native Atlantis panel before departure");
        }
        return (runtime, run);
    }

    private static object CaptureAtlantisDepartureDelivery(GameSessionRegistry registry,
        WorldInstanceRuntime runtime, DateTimeOffset now) =>
        typeof(GameSessionRegistry).GetMethod("CaptureAtlantisRunDelivery",
                BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(registry, [runtime, now]) ??
        throw new InvalidOperationException("Expected an active Atlantis delivery to delay.");

    private static async Task PublishAtlantisDepartureDeliveryAsync(
        GameSessionRegistry registry, object delivery)
    {
        var task = typeof(GameSessionRegistry).GetMethod("PublishAtlantisRunDeliveryAsync",
                BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(registry, [delivery, CancellationToken.None]) as Task ??
            throw new InvalidOperationException("Expected an Atlantis publication task.");
        await task;
    }

    private static Task FlushAtlantisDepartureAsync(ClientSession session) =>
        session.SendAsync(PacketBuilder.ServerNote("Atlantis departure test boundary"),
            CancellationToken.None, "AtlantisDepartureTestBoundary");

    private static void AssertAtlantisDepartureClear(IEnumerable<byte[]> packets)
    {
        Check.True(packets.Count(IsAtlantisDepartureClear) == 1,
            "committed Atlantis departure sends exactly one native repetition-reset clear");
    }

    private static bool IsAtlantisDepartureClear(byte[] packet) =>
        packet.SequenceEqual(PacketBuilder.RepetitionReset());

    private static bool IsAtlantisPanelPacket(byte[] packet) => ReadOpcode(packet) is
        Opcodes.RepetitionSync or Opcodes.RepetitionInstanceMembers or
        Opcodes.RepetitionFightInfo or Opcodes.RepetitionCompletionState or
        Opcodes.RepetitionPanelAction or Opcodes.RepetitionReset;

    private static void AssertAtlantisDepartureRetired(
        GameSessionRegistry registry, WorldInstanceRuntime runtime)
    {
        Check.True(runtime.Map.TryGetAtlantisRunSnapshot(out var cancelled) &&
            cancelled.State == AtlantisRunState.Cancelled && cancelled.TeamPoints == 0 &&
            !registry.TryGetWorldInstance(runtime.InstanceId, out _),
            "the next world tick cancels and retires an actually abandoned active Atlantis run");
        foreach (var field in new[] { "_atlantisUi", "_atlantisDailyLimits",
                     "_pendingAtlantisRetirements", "_atlantisDepartures" })
        {
            Check.Equal(0, AtlantisRegistryCacheCount(registry, field),
                $"abandoned Atlantis releases {field}");
        }
    }
}
