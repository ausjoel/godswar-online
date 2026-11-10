using System.Buffers.Binary;
using System.Reflection;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Game;
using Godswar.Server.Game.WorldInstances;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;

namespace Godswar.Server.ProtocolChecks;

internal static partial class InstanceCallerHandlerChecks
{
    public const string InstanceRosterStatesCheckName =
        "Instance member roster: online on entry, offline after a drop, waiting, and expiry";

    private const int RosterMemberBytes = 44;

    /// <summary>
    /// The operator's member-list table, asserted end to end on real party runs
    /// through the one shared roster implementation: a member who came in is
    /// 「在线」 (the run's leader 「在线(队长)」), a member who has not come in yet
    /// is 「等待中」, a member who drops stays on the list as 「离线」, and a member
    /// whose window has lapsed is not on the list at all. 飘渺幻境, 亚特兰蒂斯 and
    /// 港湾遇袭 are covered here; 美杜莎之岛's entry check asserts its own states.
    /// </summary>
    public static async Task RunInstanceRosterStatesAsync()
    {
        await CheckWonderlandRosterStatesAsync();
        await CheckAtlantisRosterStatesAsync();
        await CheckHarborRosterStatesAsync();
    }

    /// <summary>
    /// 飘渺幻境: the member who answered the party window before the leader comes
    /// in with him, and the member who answers it afterwards. Both are on the list
    /// as 「在线」, the second one as 「等待中」 until he arrives, and the first one
    /// stays as 「离线」 after he drops.
    /// </summary>
    private static async Task CheckWonderlandRosterStatesAsync()
    {
        await using var fixture = await CreateWonderlandHandlerFixtureAsync(
            partySize: 3);
        var party = fixture.Party;
        var leader = party.Leader;
        var early = party.Followers[0];
        var late = party.Followers[1];
        await InvokeAsync(leader.Handler, CreateActionPacket(
            InstanceCallerProtocol.WonderlandRootSubId));
        await InvokeAsync(
            leader.Handler,
            CreateActionPacket(
                InstanceCallerProtocol.WonderlandRootSubId,
                InstanceCallerProtocol.WonderlandEnterSubId),
            confirmEntry: false);
        await InvokeAsync(
            early.Handler,
            CreateRepetitionResponse(
                InstanceCallerProtocol.WonderlandClientSceneId,
                0,
                accepted: true),
            confirmEntry: false);
        await InvokeAsync(
            leader.Handler,
            CreateRepetitionResponse(
                InstanceCallerProtocol.WonderlandClientSceneId,
                0,
                accepted: true),
            confirmEntry: false);

        var instanceId = GetSourceInstanceId(leader);
        var runtime = WonderlandRuntime(leader, instanceId);
        Check.True(
            leader.Character.CurrentMap == 207 &&
            early.Character.CurrentMap == 207 &&
            late.Character.CurrentMap != 207,
            "飘渺 opens its run for the leader and the member who answered first");

        await CompleteWonderlandReadinessAsync(party);
        await leader.Registry.AdvanceMonsterWorldOnceAsync(
            WonderlandNow(runtime),
            CancellationToken.None);
        var entered = await RosterAsync(leader, WonderlandNow(runtime));
        var expectedEntered = ExpectedRoster(
            (leader.Character.Id, RepetitionMemberState.OnlineLeader),
            (early.Character.Id, RepetitionMemberState.Online),
            (late.Character.Id, RepetitionMemberState.Waiting));
        Check.True(
            entered == expectedEntered,
            "飘渺 publishes the leader as 「在线(队长)」, the member inside as " +
            "「在线」 and the member who has not answered as 「等待中」; expected " +
            $"{expectedEntered} but saw {entered}");

        // The slower member answers the same window and joins the running run.
        await InvokeAsync(
            late.Handler,
            CreateRepetitionResponse(
                InstanceCallerProtocol.WonderlandClientSceneId,
                0,
                accepted: true),
            confirmEntry: false);
        for (var tick = 0;
             tick < 4 && late.Character.CurrentMap != 207;
             tick++)
        {
            await leader.Registry.AdvanceMonsterWorldOnceAsync(
                WonderlandNow(runtime),
                CancellationToken.None);
        }

        await CompleteWonderlandReadinessAsync(party);
        await leader.Registry.AdvanceMonsterWorldOnceAsync(
            WonderlandNow(runtime),
            CancellationToken.None);
        var joined = await RosterAsync(leader, WonderlandNow(runtime));
        var expectedJoined = ExpectedRoster(
            (leader.Character.Id, RepetitionMemberState.OnlineLeader),
            (early.Character.Id, RepetitionMemberState.Online),
            (late.Character.Id, RepetitionMemberState.Online));
        Check.True(
            late.Character.CurrentMap == 207 && joined == expectedJoined,
            "the member who answers afterwards joins the run and is published as " +
            $"「在线」; expected {expectedJoined} but saw {joined}");

        // The member who came in with the leader drops. He stays on the list.
        leader.Registry.Remove(early.Session);
        await leader.Registry.AdvanceMonsterWorldOnceAsync(
            WonderlandNow(runtime),
            CancellationToken.None);
        var dropped = await RosterAsync(leader, WonderlandNow(runtime));
        var expectedDropped = ExpectedRoster(
            (leader.Character.Id, RepetitionMemberState.OnlineLeader),
            (early.Character.Id, RepetitionMemberState.Offline),
            (late.Character.Id, RepetitionMemberState.Online));
        Check.True(
            dropped == expectedDropped,
            "a 飘渺 member who drops stays on the list as 「离线」; expected " +
            $"{expectedDropped} but saw {dropped}");
    }

    /// <summary>
    /// 亚特兰蒂斯: the same table for the member who comes in with the leader and
    /// the member who answers the window afterwards.
    /// </summary>
    private static async Task CheckAtlantisRosterStatesAsync()
    {
        var daily = new ScriptedLegacyInstanceDailyEntryStore();
        await using var fixture = await CreateAtlantisOpalFixtureAsync(
            daily,
            null,
            partySize: 3);
        var leader = fixture.Leader;
        var early = fixture.Followers[0];
        var late = fixture.Followers[1];
        leader.Registry.RegisterAuthoritativeInstanceTransitionSink(
            leader.Session,
            (command, token) =>
                InvokeAuthoritativeTransitionAsync(leader.Handler, command, token));
        await OpenAtlantisPageAsync(leader);
        await InvokeAsync(
            leader.Handler,
            CreateActionPacket(
                InstanceCallerProtocol.AtlantisRootSubId,
                InstanceCallerProtocol.AtlantisEnterSubId),
            confirmEntry: false);
        await InvokeAsync(
            early.Handler,
            CreateRepetitionResponse(
                InstanceCallerProtocol.AtlantisClientSceneId,
                0,
                accepted: true),
            confirmEntry: false);
        await InvokeAsync(
            leader.Handler,
            CreateRepetitionResponse(
                InstanceCallerProtocol.AtlantisClientSceneId,
                0,
                accepted: true),
            confirmEntry: false);

        var instanceId = GetSourceInstanceId(leader);
        Check.True(
            leader.Character.CurrentMap == 205 &&
            early.Character.CurrentMap == 205 &&
            late.Character.CurrentMap != 205,
            "亚特兰蒂斯 opens its run for the leader and the member who answered " +
            "first");

        await CompleteAtlantisSceneReadinessAsync(leader.Handler);
        await CompleteAtlantisSceneReadinessAsync(early.Handler);
        await leader.Registry.AdvanceMonsterWorldOnceAsync(
            DateTimeOffset.UtcNow,
            CancellationToken.None);
        var entered = await RosterAsync(leader, DateTimeOffset.UtcNow);
        var expectedEntered = ExpectedRoster(
            (leader.Character.Id, RepetitionMemberState.OnlineLeader),
            (early.Character.Id, RepetitionMemberState.Online),
            (late.Character.Id, RepetitionMemberState.Waiting));
        Check.True(
            entered == expectedEntered,
            "亚特兰蒂斯 publishes the leader as 「在线(队长)」, the member inside " +
            "as 「在线」 and the member who has not answered as 「等待中」; " +
            $"expected {expectedEntered} but saw {entered}");

        await InvokeAsync(
            late.Handler,
            CreateRepetitionResponse(
                InstanceCallerProtocol.AtlantisClientSceneId,
                0,
                accepted: true),
            confirmEntry: false);
        for (var tick = 0;
             tick < 4 && late.Character.CurrentMap != 205;
             tick++)
        {
            await leader.Registry.AdvanceMonsterWorldOnceAsync(
                DateTimeOffset.UtcNow,
                CancellationToken.None);
        }

        await CompleteAtlantisSceneReadinessAsync(late.Handler);
        await leader.Registry.AdvanceMonsterWorldOnceAsync(
            DateTimeOffset.UtcNow,
            CancellationToken.None);
        var joined = await RosterAsync(leader, DateTimeOffset.UtcNow);
        var expectedJoined = ExpectedRoster(
            (leader.Character.Id, RepetitionMemberState.OnlineLeader),
            (early.Character.Id, RepetitionMemberState.Online),
            (late.Character.Id, RepetitionMemberState.Online));
        Check.True(
            late.Character.CurrentMap == 205 && joined == expectedJoined,
            "the 亚特兰蒂斯 member who answers afterwards joins the run and is " +
            $"published as 「在线」; expected {expectedJoined} but saw {joined}");

        leader.Registry.Remove(early.Session);
        await leader.Registry.AdvanceMonsterWorldOnceAsync(
            DateTimeOffset.UtcNow,
            CancellationToken.None);
        var dropped = await RosterAsync(leader, DateTimeOffset.UtcNow);
        var expectedDropped = ExpectedRoster(
            (leader.Character.Id, RepetitionMemberState.OnlineLeader),
            (early.Character.Id, RepetitionMemberState.Offline),
            (late.Character.Id, RepetitionMemberState.Online));
        Check.True(
            dropped == expectedDropped,
            "an 亚特兰蒂斯 member who drops stays on the list as 「离线」; " +
            $"expected {expectedDropped} but saw {dropped}");
    }

    /// <summary>
    /// 港湾遇袭: the same table, plus the window's own expiry, which takes a member
    /// who never answered off the list entirely.
    /// </summary>
    private static async Task CheckHarborRosterStatesAsync()
    {
        using var open = InstanceEntrySwitchScope.Open(); // INSTANCES
        var daily = new ScriptedLegacyInstanceDailyEntryStore();
        await using var fixture = await CreateAtlantisOpalFixtureAsync(
            daily,
            null,
            partySize: 3);
        foreach (var character in fixture.Characters)
        {
            character.Level = 55;
        }
        var leader = fixture.Leader;
        var waiting = fixture.Followers[0];
        var entered = fixture.Followers[1];
        leader.Registry.RegisterAuthoritativeInstanceTransitionSink(
            leader.Session,
            (command, token) =>
                InvokeAuthoritativeTransitionAsync(leader.Handler, command, token));

        await InvokeAsync(leader.Handler, CreateActionPacket(
            InstanceCallerProtocol.HarborAttackRootSubId));
        await InvokeAsync(
            leader.Handler,
            CreateActionPacket(
                InstanceCallerProtocol.HarborAttackRootSubId,
                InstanceCallerProtocol.HarborAttackPageSubId),
            confirmEntry: false);
        await InvokeAsync(
            entered.Handler,
            CreateRepetitionResponse(
                InstanceCallerProtocol.HarborAttackFirstClientSceneId,
                0,
                accepted: true),
            confirmEntry: false);
        await InvokeAsync(
            leader.Handler,
            CreateRepetitionResponse(
                InstanceCallerProtocol.HarborAttackFirstClientSceneId,
                0,
                accepted: true),
            confirmEntry: false);
        await CompleteAtlantisSceneReadinessAsync(leader.Handler);
        await CompleteAtlantisSceneReadinessAsync(entered.Handler);

        var instanceId = GetSourceInstanceId(leader);
        Check.True(
            entered.Character.CurrentMap ==
                InstanceCallerProtocol.HarborAttackFirstMapId &&
            waiting.Character.CurrentMap == leader.SourceMapId &&
            leader.Registry.TryGetSessionWorldInstanceId(
                waiting.Session,
                out var waitingInstanceId) &&
            waitingInstanceId != instanceId,
            "港湾遇袭 admits only the members who answered the window");

        var publishedAt = DateTimeOffset.UtcNow;
        await leader.Registry.AdvanceMonsterWorldOnceAsync(
            publishedAt,
            CancellationToken.None);
        var beforeExpiry = await RosterAsync(leader, publishedAt);
        var expectedBeforeExpiry = ExpectedRoster(
            (leader.Character.Id, RepetitionMemberState.OnlineLeader),
            (entered.Character.Id, RepetitionMemberState.Online),
            (waiting.Character.Id, RepetitionMemberState.Waiting));
        Check.True(
            beforeExpiry == expectedBeforeExpiry,
            "港湾遇袭 publishes the leader as 「在线(队长)」, the member inside as " +
            "「在线」 and the member who has not answered as 「等待中」; expected " +
            $"{expectedBeforeExpiry} but saw {beforeExpiry}");

        // The window's own sixty seconds lapse: the member who never answered is
        // no longer waiting, so the list no longer carries him at all.
        await leader.Registry.AdvanceMonsterWorldOnceAsync(
            publishedAt.AddSeconds(61),
            CancellationToken.None);
        var afterExpiry = await RosterAsync(
            leader,
            publishedAt.AddSeconds(61));
        var expectedAfterExpiry = ExpectedRoster(
            (leader.Character.Id, RepetitionMemberState.OnlineLeader),
            (entered.Character.Id, RepetitionMemberState.Online));
        Check.True(
            afterExpiry == expectedAfterExpiry,
            "a member whose Enter window has lapsed is not on the roster at all; " +
            $"expected {expectedAfterExpiry} but saw {afterExpiry}");

        // The member inside drops: he stays on the list as offline.
        leader.Registry.Remove(entered.Session);
        await leader.Registry.AdvanceMonsterWorldOnceAsync(
            publishedAt.AddSeconds(62),
            CancellationToken.None);
        var dropped = await RosterAsync(leader, publishedAt.AddSeconds(62));
        var expectedDropped = ExpectedRoster(
            (leader.Character.Id, RepetitionMemberState.OnlineLeader),
            (entered.Character.Id, RepetitionMemberState.Offline));
        Check.True(
            dropped == expectedDropped,
            "a 港湾遇袭 member who drops stays on the list as 「离线」; expected " +
            $"{expectedDropped} but saw {dropped}");
    }

    /// <summary>
    /// The runtime of the 飘渺 run the leader just entered, so its own world clock
    /// drives the ticks the roster is published from.
    /// </summary>
    private static WorldInstanceRuntime WonderlandRuntime(
        InstanceCallerFixture leader,
        WorldInstanceId instanceId)
    {
        var directory = (LocalWorldInstanceRuntimeDirectory)typeof(GameSessionRegistry)
            .GetProperty("WorldInstances", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(leader.Registry)!;
        return directory.TryFind(instanceId, out var runtime)
            ? runtime
            : throw new InvalidOperationException(
                "The 飘渺 roster fixture found no runtime.");
    }

    /// <summary>
    /// The roster the run publishes to this member from the next tick on, as
    /// <c>characterId:state</c> rows in publication order, or a description of
    /// what was missing. A rejected exact batch is retried by a later tick, so the
    /// roster is read from every tick until one carries it.
    /// </summary>
    private static async Task<string> RosterAsync(
        InstanceCallerFixture member,
        DateTimeOffset now)
    {
        var seenBefore = member.ReadPackets().Count;
        for (var attempt = 0; attempt < 4; attempt++)
        {
            await member.Registry.AdvanceMonsterWorldOnceAsync(
                now,
                CancellationToken.None);
            var published = LastRosterPacket(
            [
                .. member.ReadPackets().Skip(seenBefore)
            ]);
            if (published is not null)
            {
                return RosterFacts(published);
            }
        }

        return "<no roster packet was published>";
    }

    /// <summary>
    /// The expected rows as <c>characterId:state</c>, in the roster's own order,
    /// with the in-game confirmed bytes: 0 offline, 1 online (the run's leader
    /// included, because 「在线」 and 「在线(队长)」 share the one byte the client
    /// draws), 2 waiting.
    /// </summary>
    private static string ExpectedRoster(
        params (int CharacterId, RepetitionMemberState State)[] rows) =>
        string.Join(' ', rows
            .OrderBy(static row => row.CharacterId)
            .Select(static row => $"{row.CharacterId}:{(byte)row.State}"));

    private static string RosterFacts(byte[] packet)
    {
        var count = RosterCount(packet);
        var rows = new string[count];
        for (var index = 0; index < count; index++)
        {
            rows[index] = $"{RosterCharacterId(packet, index)}:" +
                $"{RosterState(packet, index)}";
        }

        return rows.Length == 0 ? "<empty roster>" : string.Join(' ', rows);
    }

    private static byte[]? LastRosterPacket(IReadOnlyList<byte[]> packets) =>
        packets
            .Where(static packet => packet.Length >= 8 &&
                BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(2)) ==
                    Opcodes.RepetitionInstanceMembers)
            .LastOrDefault();

    private static int RosterCount(byte[] packet) =>
        BinaryPrimitives.ReadInt32LittleEndian(packet.AsSpan(4));

    private static int RosterCharacterId(byte[] packet, int index) =>
        BinaryPrimitives.ReadInt32LittleEndian(
            packet.AsSpan(8 + index * RosterMemberBytes));

    private static byte RosterState(byte[] packet, int index) =>
        packet[8 + index * RosterMemberBytes + 40];
}
