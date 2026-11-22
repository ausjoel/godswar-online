using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Game;
using Godswar.Server.Game.WorldInstances;

namespace Godswar.Server.ProtocolChecks;

/// <summary>
/// S5 (5 Nov party run b87f263a): the registry side of a late join. The island-clear evidence must
/// carry each late member's own reservation, keep the run identity of the registered party, and
/// leave out a late member whose join was released before it was admitted.
/// </summary>
internal static partial class InstanceCallerHandlerChecks
{
    public const string WonderlandLateJoinerCheckName =
        "Wonderland island evidence carries a late member's own reservation and drops a released join";

    public static async Task RunWonderlandLateJoinerAsync()
    {
        await CheckReleasedThenRejoinedAsync();
        await CheckReleasedWithoutRejoinAsync();
    }

    private static async Task CheckReleasedThenRejoinedAsync()
    {
        await using var fixture = await CreateWonderlandHandlerFixtureAsync(partySize: 3,
            failedFollowers: new HashSet<int> { 2 });
        var party = fixture.Party;
        var registry = party.Leader.Registry;
        var runtime = await EnterWonderlandHandlerAsync(fixture);
        var partyReservation = fixture.Daily.Claims.Single().ReservationId;
        await ClearWonderlandHandlerIslandAsync(fixture, runtime);
        var first = fixture.Titles.Requests.Single(request => request.IslandNumber == 1);
        var registered = first.AdmittedCharacterIds.ToArray();
        Check.True(fixture.Titles.Requests.All(request => request.IslandNumber == 1),
            "only island one has cleared before the late join: " +
            string.Join(',', fixture.Titles.Requests.Select(request => request.IslandNumber)));

        var late = party.Followers[1];
        Check.True(!registered.Contains(late.Character.Id), "the slower member missed the sealed entry");
        var released = Guid.NewGuid();
        Check.True(registry.TryAdmitWonderlandLateEntrant(runtime.InstanceId, late.Session, late.Character.Id,
                late.Character.Level, late.Character.Camp, released),
            "the slower member is appended to the sealed run");
        registry.ForgetWonderlandLateReservation(released); // the queued join expired / was refused
        var rejoined = Guid.NewGuid();
        Check.True(registry.TryAdmitWonderlandLateEntrant(runtime.InstanceId, late.Session, late.Character.Id,
                late.Character.Level, late.Character.Camp, rejoined),
            "a second confirmation on a fresh reservation is accepted");
        var stale = Guid.NewGuid();
        Check.True(registry.TryAdmitWonderlandLateEntrant(runtime.InstanceId, late.Session, late.Character.Id,
                late.Character.Level, late.Character.Camp, stale),
            "a repeated confirmation is idempotent");
        registry.ForgetWonderlandLateReservation(stale); // its spare reservation is given straight back
        await TransferLateMemberAsync(registry, late, runtime);

        await ClearWonderlandHandlerIslandAsync(fixture, runtime);
        var second = fixture.Titles.Requests.LastOrDefault(request => request.IslandNumber == 2);
        Check.True(second is not null, "island two clears and queues its title evidence");
        var all = registered.Append(late.Character.Id).Order().ToArray();
        Check.True(second!.AdmittedCharacterIds.SequenceEqual(all) && second.CharacterIds.SequenceEqual(all),
            $"the late member is admitted and paid for the island cleared after joining " +
            $"(admitted {string.Join(',', second.AdmittedCharacterIds)}, paid {string.Join(',', second.CharacterIds)}, " +
            $"expected {string.Join(',', all)})");
        Check.True(second.PartyCharacterIds.SequenceEqual(registered) &&
            second.AdmissionReservationId == partyReservation &&
            second.LateAdmissionReservations.Count == 1 &&
            second.LateAdmissionReservations[late.Character.Id] == rejoined &&
            second.AdmissionReservationIds.Contains(rejoined) && !second.AdmissionReservationIds.Contains(released) &&
            !second.AdmissionReservationIds.Contains(stale),
            "the late member is fenced by the reservation that admitted them, not a released or spare one");
        Check.True(second.RunHash == first.RunHash, "the run identity is unchanged by the late join");
        Check.True(!registry.HasPendingWonderlandTitles(runtime.InstanceId), "both islands settle");
    }

    private static async Task CheckReleasedWithoutRejoinAsync()
    {
        await using var fixture = await CreateWonderlandHandlerFixtureAsync(partySize: 3,
            failedFollowers: new HashSet<int> { 2 });
        var party = fixture.Party;
        var registry = party.Leader.Registry;
        var runtime = await EnterWonderlandHandlerAsync(fixture);
        await ClearWonderlandHandlerIslandAsync(fixture, runtime);
        var registered = fixture.Titles.Requests.Single(request => request.IslandNumber == 1).AdmittedCharacterIds.ToArray();
        var late = party.Followers[1];
        var released = Guid.NewGuid();
        Check.True(registry.TryAdmitWonderlandLateEntrant(runtime.InstanceId, late.Session, late.Character.Id,
                late.Character.Level, late.Character.Camp, released),
            "the slower member is appended to the sealed run");
        registry.ForgetWonderlandLateReservation(released);

        await ClearWonderlandHandlerIslandAsync(fixture, runtime);
        var second = fixture.Titles.Requests.LastOrDefault(request => request.IslandNumber == 2);
        Check.True(second is not null && second.AdmittedCharacterIds.SequenceEqual(registered) &&
            second.LateAdmissionReservations.Count == 0,
            "a late member whose join was released holds no ledger row and is left off the admitted roster");
        Check.True(!registry.HasPendingWonderlandTitles(runtime.InstanceId),
            "the island still settles for the registered party");
    }

    private static async Task TransferLateMemberAsync(GameSessionRegistry registry, AtlantisOpalFollower member,
        WorldInstanceRuntime runtime)
    {
        var source = registry.GetMapSessions(member.Character.CurrentMap)
            .Single(context => ReferenceEquals(context.Session, member.Session));
        var entrance = WonderlandTerrainPolicy.GetIsland(1).Entrance;
        var command = new AuthoritativeInstanceTransitionCommand(member.Character.Id,
            source.WorldInstanceId, source.MapId, source.Ownership, runtime.InstanceId, 207, entrance.X, entrance.Z);
        Check.True(await InvokeAuthoritativeTransitionAsync(member.Handler, command, CancellationToken.None) &&
            member.Character.CurrentMap == 207,
            "the late member transfers into the exact run");
        // The follower fixture has no pinned map-207 content for the client-ready packet path, so the
        // registry's own readiness transition is used directly, having "seen" everyone on the map.
        var ready = registry.TryMarkWorldReady(member.Session, new Dictionary<uint, long>(), out var unseen);
        if (!ready)
            ready = registry.TryMarkWorldReady(member.Session,
                unseen.ToDictionary(context => context.ObjectId, context => context.WorldRevision), out _);
        Check.True(ready,
            "the late member becomes world-ready inside the run");
    }
}
