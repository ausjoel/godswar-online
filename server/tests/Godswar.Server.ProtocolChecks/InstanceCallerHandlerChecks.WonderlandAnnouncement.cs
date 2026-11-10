using System.Buffers.Binary;
using System.Reflection;
using Godswar.Server.Application.WorldInstances;
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
    public const string WonderlandAnnouncementCheckName =
        "Wonderland terminal announcements preserve settlement and audience scopes without island popups";

    public static async Task RunWonderlandAnnouncementAsync()
    {
        await CheckWonderlandCompletionAnnouncementAsync();
        await CheckWonderlandUnfinishedAnnouncementsAsync();
    }

    private static async Task CheckWonderlandCompletionAnnouncementAsync()
    {
        await using var fixture = await CreateWonderlandHandlerFixtureAsync(partySize: 2);
        var party = fixture.Party;
        var leader = party.Leader;
        var registry = leader.Registry;
        var origin = GetSourceInstanceId(leader);
        var originX = leader.Character.PositionX;
        var originZ = leader.Character.PositionZ;
        await using var sameFaction = await WonderlandNoticeListener.CreateAsync(registry, 91_101,
            leader.Character.Camp, leader.Character.RealmId);
        await using var otherFaction = await WonderlandNoticeListener.CreateAsync(registry, 91_102,
            leader.Character.Camp == GameDefaults.SpartaCamp ? GameDefaults.AthensCamp : GameDefaults.SpartaCamp,
            leader.Character.RealmId);
        await using var otherRealm = await WonderlandNoticeListener.CreateAsync(registry, 91_103,
            leader.Character.Camp, RealmId.Dwargon);
        var completedInstances = new HashSet<WorldInstanceId>();
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var before = party.ReadAllPackets().Select(p => p.Count).ToArray();
            var runtime = await EnterWonderlandHandlerAsync(fixture);
            Check.True(completedInstances.Add(runtime.InstanceId), "repeat entry creates a distinct exact Wonderland run");
            for (var island = 1; island <= 7; island++)
            {
                await ClearWonderlandHandlerIslandAsync(fixture, runtime);
                Check.True(party.ReadAllPackets().All(p => p.Count(IsWonderlandCenteredNotice) == attempt) &&
                    sameFaction.Packets.Count(IsWonderlandCenteredNotice) == attempt &&
                    otherFaction.Packets.All(p => !IsWonderlandCenteredNotice(p)) &&
                    otherRealm.Packets.All(p => !IsWonderlandCenteredNotice(p)),
                    $"island{island} produces no centered completion announcement for any audience");
                Check.True(party.Characters.All(character =>
                        character.OwnedTitleIds.Contains(WonderlandTitlePolicy.Resolve(island).TitleId)) &&
                    party.ReadAllPackets().Select((packets, index) => packets.Skip(before[index])
                        .All(packet => ReadOpcode(packet) != Opcodes.ServerNote)).All(value => value),
                    "island progress and earned titles update normal UI without notification boxes");
            }
            runtime.Map.TryGetWonderlandSnapshot(out var final);
            Check.True(final.RequiredMonstersRemaining == 5, "announcement requires all four bosses and the chest guard");
            foreach (var boss in final.ActiveSpawns.Where(p => p.IsBoss))
                KillWonderlandHandlerMonster(fixture, runtime, boss.ObjectId);
            Check.True(!registry.HasPendingWonderlandTitles(runtime.InstanceId) &&
                sameFaction.Packets.Count(IsWonderlandCenteredNotice) == attempt,
                "four final bosses alone cannot announce completion while the chest guard remains");
            KillWonderlandHandlerMonster(fixture, runtime,
                final.ActiveSpawns.Single(p => p.Role == WonderlandMonsterRole.ChestGuard).ObjectId);
            runtime.Map.TryGetWonderlandSnapshot(out var completed);
            Check.True(completed.State == WonderlandRunState.Completed && registry.HasPendingWonderlandTitles(runtime.InstanceId),
                "the chest guard freezes completed-run entitlement before the after-exit announcement");
            fixture.Titles.FailuresRemaining = 1;
            await registry.RetryWonderlandTitlesAsync(WonderlandNow(runtime), CancellationToken.None);
            await FlushWonderlandNoticeAudienceAsync(party, sameFaction, otherFaction, otherRealm);
            Check.True(registry.HasPendingWonderlandTitles(runtime.InstanceId) &&
                party.ReadAllPackets().All(p => p.Count(IsWonderlandCenteredNotice) == attempt) &&
                sameFaction.Packets.Count(IsWonderlandCenteredNotice) == attempt,
                "a final-title persistence outage prevents premature completion announcements");
            await registry.RetryWonderlandTitlesAsync(WonderlandNow(runtime), CancellationToken.None);
            await FlushWonderlandNoticeAudienceAsync(party, sameFaction, otherFaction, otherRealm);
            var expected = PacketBuilder.CenteredAnnouncement(GameSessionRegistry.BuildWonderlandCompletionAnnouncement(
                leader.Character.Name, solo: false));
            Check.True(!registry.HasPendingWonderlandTitles(runtime.InstanceId) &&
                party.ReadAllPackets().All(p => p.Count(packet => packet.SequenceEqual(expected)) == attempt) &&
                sameFaction.Packets.Count(packet => packet.SequenceEqual(expected)) == attempt &&
                otherFaction.Packets.All(p => !IsWonderlandCenteredNotice(p)) &&
                otherRealm.Packets.All(p => !IsWonderlandCenteredNotice(p)),
                "successful final settlement retains the notice while the party is still inside");
            var receipt = fixture.Titles.AppliedReceipts.Single(r =>
                r.WorldInstanceId == runtime.InstanceId && r.Award.IslandNumber == 8);
            Check.True(receipt.Members.All(member => member.NewlyOwned == (attempt == 0)),
                "the second completed run announces even though no member newly owns the final title");
            Check.True(party.Characters.Select((character, index) =>
                character.SelectedTitleId == (index == 0 ? 0u : 5009u) && character.MedusaHonorPoints == 1234).All(v => v),
                "completion announcement preserves selected titles and wallet");
            var replay = fixture.Titles.Requests.Last(r => r.WorldInstanceId == runtime.InstanceId && r.IslandNumber == 8);
            Check.True(registry.QueueWonderlandTitleMilestone(replay), "same completed-run entitlement replay stays accepted");
            await registry.RetryWonderlandTitlesAsync(WonderlandNow(runtime), CancellationToken.None);
            await registry.RetryWonderlandTitlesAsync(WonderlandNow(runtime), CancellationToken.None);
            await FlushWonderlandNoticeAudienceAsync(party, sameFaction, otherFaction, otherRealm);
            Check.True(party.ReadAllPackets().All(p => p.Count(IsWonderlandCenteredNotice) == attempt) &&
                sameFaction.Packets.Count(IsWonderlandCenteredNotice) == attempt,
                "settlement replay cannot announce before the party exits");
            await ReturnWonderlandPartyToCallerAsync(party, origin, leader.SourceMapId, originX, originZ,
                async (memberIndex, ready) =>
                {
                    await registry.AdvanceMonsterWorldOnceAsync(WonderlandNow(runtime), CancellationToken.None);
                    await FlushWonderlandNoticeAudienceAsync(party, sameFaction, otherFaction, otherRealm);
                    var count = attempt + (memberIndex == party.Characters.Count - 1 && ready ? 1 : 0);
                    Check.True(party.ReadAllPackets().All(p => p.Count(packet => packet.SequenceEqual(expected)) == count) &&
                        sameFaction.Packets.Count(packet => packet.SequenceEqual(expected)) == count &&
                        otherFaction.Packets.All(p => !IsWonderlandCenteredNotice(p)) &&
                        otherRealm.Packets.All(p => !IsWonderlandCenteredNotice(p)),
                        "completion waits for every party member to exit and load the destination, then announces to its scoped audience");
                });
            await registry.AdvanceMonsterWorldOnceAsync(WonderlandNow(runtime), CancellationToken.None);
            await FlushWonderlandNoticeAudienceAsync(party, sameFaction, otherFaction, otherRealm);
            Check.True(party.ReadAllPackets().All(p => p.Count(IsWonderlandCenteredNotice) == attempt + 1) &&
                sameFaction.Packets.Count(IsWonderlandCenteredNotice) == attempt + 1,
                "later world passes cannot duplicate an exited-run announcement");
        }
    }

    private static async Task CheckWonderlandUnfinishedAnnouncementsAsync()
    {
        foreach (var timeout in new[] { false, true })
        {
            await using var fixture = await CreateWonderlandHandlerFixtureAsync();
            var leader = fixture.Party.Leader;
            await using var sameFaction = await WonderlandNoticeListener.CreateAsync(leader.Registry, 91_111,
                leader.Character.Camp, leader.Character.RealmId);
            await using var otherFaction = await WonderlandNoticeListener.CreateAsync(leader.Registry, 91_112,
                leader.Character.Camp == GameDefaults.SpartaCamp ? GameDefaults.AthensCamp : GameDefaults.SpartaCamp,
                leader.Character.RealmId);
            await using var otherRealm = await WonderlandNoticeListener.CreateAsync(leader.Registry, 91_113,
                leader.Character.Camp, RealmId.Dwargon);
            var runtime = await EnterWonderlandHandlerAsync(fixture);
            await ClearWonderlandHandlerIslandAsync(fixture, runtime);
            runtime.Map.TryGetWonderlandSnapshot(out var active);
            var before = leader.ReadPackets().Count;
            if (!timeout) await InvokeAsync(leader.Handler, CreateRepetitionLeave(227, 0));
            await leader.Registry.AdvanceMonsterWorldOnceAsync(timeout ? active.Deadline : WonderlandNow(runtime), CancellationToken.None);
            await leader.Session.SendAsync(PacketBuilder.RepetitionReset(), CancellationToken.None);
            var expected = PacketBuilder.CenteredAnnouncement(GameSessionRegistry.BuildWonderlandTerminationAnnouncement(
                leader.Character.Name, true, 1, timeout));
            Check.True(leader.Character.CurrentMap != 207 &&
                leader.ReadPackets().All(packet => !packet.SequenceEqual(expected)) &&
                leader.ReadPackets().Skip(before).All(packet => ReadOpcode(packet) != Opcodes.ServerNote),
                "termination or timeout exits without a popup or a notice lost during destination loading");
            Check.True(!leader.Registry.TryGetWorldInstance(runtime.InstanceId, out _),
                "empty runtime retirement proceeds while the destination is loading");
            await CompleteAtlantisSceneReadinessAsync(leader.Handler);
            await leader.Registry.AdvanceMonsterWorldOnceAsync(WonderlandNow(runtime), CancellationToken.None);
            await FlushWonderlandNoticeAudienceAsync(fixture.Party, sameFaction, otherFaction, otherRealm);
            Check.True(leader.ReadPackets().Count(IsWonderlandCenteredNotice) == 1 &&
                sameFaction.Packets.Count(packet => packet.SequenceEqual(expected)) == 1 &&
                otherFaction.Packets.All(packet => !IsWonderlandCenteredNotice(packet)) &&
                otherRealm.Packets.All(packet => !IsWonderlandCenteredNotice(packet)),
                "ended-run announcement survives runtime retirement and reaches only its realm and faction after loading");
            await leader.Registry.AdvanceMonsterWorldOnceAsync(WonderlandNow(runtime), CancellationToken.None);
            await FlushWonderlandNoticeAudienceAsync(fixture.Party, sameFaction, otherFaction, otherRealm);
            Check.True(leader.ReadPackets().Count(IsWonderlandCenteredNotice) == 1 &&
                sameFaction.Packets.Count(IsWonderlandCenteredNotice) == 1,
                "retired run notice is removed after its single publication");
        }
    }

    private static bool IsWonderlandCenteredNotice(byte[] packet) => packet.Length == 137 &&
        ReadOpcode(packet) == Opcodes.PythonNote && BinaryPrimitives.ReadInt32LittleEndian(packet.AsSpan(4)) == 50 && packet[8] == 0;

    private static async Task FlushWonderlandNoticeAudienceAsync(AtlantisOpalFixture party,
        params WonderlandNoticeListener[] listeners)
    {
        foreach (var session in party.Sessions.Concat(listeners.Select(l => l.Session)))
            await session.SendAsync(PacketBuilder.ServerNote("Wonderland notice test boundary"), CancellationToken.None);
    }

    private static async Task ReturnWonderlandPartyToCallerAsync(AtlantisOpalFixture party, WorldInstanceId target,
        byte map, float x, float z, Func<int, bool, Task> observeExit)
    {
        var directory = (LocalWorldInstanceRuntimeDirectory)typeof(GameSessionRegistry)
            .GetProperty("WorldInstances", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(party.Leader.Registry)!;
        var ordinaryTransition = typeof(GameClientHandler).GetMethod("TryBeginMapTransitionAsync",
            BindingFlags.Instance | BindingFlags.NonPublic, binder: null,
            [typeof(byte), typeof(float), typeof(float), typeof(string), typeof(CancellationToken)], modifiers: null)!;
        var actors = new[] { (party.Leader.Session, party.Leader.Character, party.Leader.Handler) }
            .Concat(party.Followers.Select(f => (f.Session, f.Character, f.Handler)));
        var memberIndex = 0;
        foreach (var (session, character, handler) in actors)
        {
            Check.True(party.Leader.Registry.TryGetSessionWorldInstanceId(session, out var source),
                "repeat-run actor retains exact source membership");
            var context = party.Leader.Registry.GetWorldInstanceSessions(source)
                .Single(c => ReferenceEquals(c.Session, session));
            var capitalMap = character.Camp == GameDefaults.SpartaCamp
                ? GameDefaults.SpartaCapitalMap : GameDefaults.AthensCapitalMap;
            var capital = (await directory.GetOrCreateOpenWorldAsync(character.RealmId, capitalMap,
                200, DateTimeOffset.UtcNow, CancellationToken.None)).Runtime ??
                throw new InvalidOperationException("Repeat-run faction capital was unavailable.");
            Check.True(await InvokeAuthoritativeTransitionAsync(handler,
                new(character.Id, context.WorldInstanceId, context.MapId, context.Ownership,
                    capital.InstanceId, capitalMap, GameDefaults.StartingPositionX, GameDefaults.StartingPositionZ),
                CancellationToken.None), "finished party exits to its permitted faction capital through the actual handler");
            await observeExit(memberIndex, false);
            await CompleteAtlantisSceneReadinessAsync(handler);
            await observeExit(memberIndex++, true);
            var returned = (Task<SceneTransitionOutcome>)ordinaryTransition.Invoke(handler,
                [map, x, z, "WonderlandRepeatNoticeTest", CancellationToken.None])!;
            Check.True(await returned == SceneTransitionOutcome.CommittedAwaitingReadiness,
                "ordinary handler travel returns the same actor from its capital to the test caller map");
            await CompleteAtlantisSceneReadinessAsync(handler);
            Check.True(party.Leader.Registry.TryGetSessionWorldInstanceId(session, out var returnedTo) && returnedTo == target,
                "repeat entry uses the original caller instance and current ownership");
        }
        var refresh = FindHandlerMethod("RefreshNearbyWorldObjectsAsync").Invoke(party.Leader.Handler,
            ["WonderlandRepeatNoticeTest", CancellationToken.None]) as Task;
        await refresh!;
    }

    private sealed record WonderlandNoticeListener(GameSessionRegistry Registry, ClientSession Session,
        FactionCrierCaptureTransport Transport) : IAsyncDisposable
    {
        public IReadOnlyList<byte[]> Packets => Transport.ReadLegacyPackets();

        public static async Task<WonderlandNoticeListener> CreateAsync(GameSessionRegistry registry, int id,
            byte camp, RealmId realm)
        {
            var transport = new FactionCrierCaptureTransport();
            var session = new ClientSession(transport);
            var character = new GameCharacter { Id = id, AccountId = id, Name = $"Notice{id}", RealmId = realm,
                Camp = camp, CurrentMap = 0, CurrentHp = 1000, MaxHp = 1000, PositionX = 2000, PositionZ = 2000 };
            GameHandlerOwnershipTestFences.Bind(registry, session, character.AccountId, character);
            // The normal process API rejects foreign realms. A directory-level
            // fixture creates an exact foreign context to test the publisher's defense.
            var directory = (LocalWorldInstanceRuntimeDirectory)typeof(GameSessionRegistry)
                .GetProperty("WorldInstances", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(registry)!;
            var created = await directory.GetOrCreateOpenWorldAsync(realm, 0, 20, DateTimeOffset.UtcNow, CancellationToken.None);
            var runtime = created.Runtime ?? throw new InvalidOperationException("Notice listener world was unavailable.");
            registry.JoinWorldInstance(session, id, character, WorldObjectIds.ForPlayer(id), runtime.InstanceId);
            return new(registry, session, transport);
        }

        public async ValueTask DisposeAsync()
        {
            Registry.Remove(Session);
            await Session.DisposeAsync();
        }
    }
}
