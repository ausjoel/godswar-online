using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Game;
using Godswar.Server.Networking;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static class WonderlandTitleChecks
{
    public const string CheckName = "Wonderland title milestones freeze eligibility and preserve selection on retry";

    public static async Task RunAsync()
    {
        var at = new DateTimeOffset(2026, 9, 10, 0, 0, 0, TimeSpan.Zero);
        WonderlandTitleMember[] members = [new(1, 11, PlayerOwnershipTestFences.ForCharacter(11)),
            new(2, 22, PlayerOwnershipTestFences.ForCharacter(22))];
        var instance = WorldInstanceId.New();
        var reservation = Guid.NewGuid();
        WonderlandTitleRequest Request(int island, IReadOnlyCollection<WonderlandTitleMember> admitted,
            IReadOnlyCollection<WonderlandTitleMember> frozen) =>
            new(instance, RealmId.Tempest, reservation, at, at.AddMinutes(island), island, admitted, frozen);
        var request = Request(2, members, [members[0]]);
        var replay = Request(2, members.Reverse().ToArray(), [members[0]]);
        Check.True(request.RunHash == replay.RunHash && request.RequestHash == replay.RequestHash &&
            request.AdmittedMembers.Count == 2 && request.FrozenMembers.Count == 1,
            "canonical evidence preserves original admission independently of eligible island finishers");
        Check.True(new[] { 2, 4, 6, 7, 8 }.Select(island => WonderlandTitlePolicy.Resolve(island).TitleId)
            .SequenceEqual(new uint[] { 5114, 5115, 5116, 5117, 5118 }), "previously earned titles retain their original island identities");
        var allIslands = Enumerable.Range(1, 8).Select(island => Request(island, members, members)).ToArray();
        Check.True(allIslands.Select(value => value.Award.TitleId).SequenceEqual(
            new uint[] { 5155, 5114, 5156, 5115, 5157, 5116, 5117, 5118 }),
            "new island titles use unused client IDs without reassigning any earlier title");
        Check.True(allIslands.Select(value => value.Award.TitleId).Distinct().Count() == 8 &&
            allIslands.Select(value => value.Award.DisplayName).Distinct().Count() == 8 &&
            allIslands.Select(value => value.RunHash).Distinct().Single() == request.RunHash &&
            allIslands.Select(value => value.RequestHash).Distinct().Count() == 8,
            "every island grants a distinct title receipt within the same immutable admitted run");
        foreach (var island in new[] { 0, 9 })
            Check.Throws<ArgumentOutOfRangeException>(() => Request(island, members, members),
                "islands outside the eight-island run cannot mint titles");
        Check.Throws<ArgumentException>(() => Request(2, members, [new(3, 33, PlayerOwnershipTestFences.ForCharacter(33))]),
            "outsiders cannot enter the frozen eligible roster");
        Check.Throws<ArgumentException>(() => Request(2, members, [members[0] with { Ownership = default }]),
            "an eligible finisher requires captured session ownership");
        Check.Throws<ArgumentException>(() => new WonderlandTitleRequest(instance, RealmId.Tempest, reservation,
            at, at.AddMinutes(40), 8, members, members), "the deadline excludes late milestone awards");
        Check.True(Request(2, members, members).RequestHash != request.RequestHash &&
            Request(4, members, members).RunHash == request.RunHash,
            "each island freezes its own finishers while retaining immutable run identity");

        var transport = new ScriptedLegacyByteTransport();
        await using var session = new ClientSession(transport);
        var character = new GameCharacter
        {
            Id = 11, AccountId = 1, Name = "WonderlandTitles", CurrentMap = 7, Level = 120,
            CurrentHp = 1000, MaxHp = 1000, CurrentMp = 100, MaxMp = 100,
            SelectedTitleId = 5009, MedusaHonorPoints = 6000, MedusaRewardRevision = 20
        };
        character.AddOwnedTitle(5009);
        var registry = new GameSessionRegistry();
        GameHandlerOwnershipTestFences.Bind(registry, session, 1, character);
        registry.JoinMap(session, 1, character, WorldObjectIds.ForPlayer(11));
        var store = new LostAcknowledgementStore();
        registry.ConfigureWonderlandTitles(store);
        Check.True(registry.QueueWonderlandTitleMilestone(request) && registry.QueueWonderlandTitleMilestone(replay) &&
            !registry.QueueWonderlandTitleMilestone(Request(2, members, members)),
            "queue accepts exact retries but never changes a frozen milestone roster");
        await registry.RetryWonderlandTitlesAsync(at.AddMinutes(3), CancellationToken.None);
        Check.True(store.Commits == 1 && registry.HasPendingWonderlandTitles(instance) &&
            !registry.ForgetSettledWonderlandTitles(instance) && !character.OwnedTitleIds.Contains(5114u),
            "lost commit acknowledgement retains pending evidence and blocks retirement without speculative ownership");
        await registry.RetryWonderlandTitlesAsync(at.AddMinutes(3).AddSeconds(1), CancellationToken.None);
        Check.True(store.Commits == 1 && store.Calls == 2 && !registry.HasPendingWonderlandTitles(instance) &&
            character.OwnedTitleIds.SequenceEqual(new uint[] { 5009, 5114 }) &&
            character.SelectedTitleId == 5009 && character.MedusaHonorPoints == 6000 && character.MedusaRewardRevision == 20,
            "duplicate durable receipt grants ownership once without replacing newer wallet, revision, or selection");
        Check.True(registry.QueueWonderlandTitleMilestone(request), "settled replay remains accepted");
        await registry.RetryWonderlandTitlesAsync(at.AddMinutes(3).AddSeconds(2), CancellationToken.None);
        Check.Equal(2, store.Calls, "cached settlement publishes no repeated title notice");

        registry.Remove(session);
        var later = Request(4, members, members);
        Check.True(registry.QueueWonderlandTitleMilestone(later), "later milestone retains all captured finishers");
        await registry.RetryWonderlandTitlesAsync(at.AddMinutes(5), CancellationToken.None);
        Check.True(store.LastRequest == later && store.LastRequest.FrozenMembers.Count == 2 &&
            !registry.HasPendingWonderlandTitles(instance) && registry.ForgetSettledWonderlandTitles(instance),
            "post-clear departure cannot discard frozen entitlement even when no recipient remains online");
    }

    private sealed class LostAcknowledgementStore : IWonderlandTitleStore
    {
        private readonly HashSet<string> _committed = [];
        public int Calls { get; private set; }
        public int Commits => _committed.Count;
        public WonderlandTitleRequest? LastRequest { get; private set; }
        public Task<WonderlandTitleReceipt> SettleAsync(WonderlandTitleRequest request, CancellationToken cancellationToken = default)
        {
            Calls++;
            LastRequest = request;
            var added = _committed.Add(request.RequestHash);
            if (Calls == 1) throw new IOException("Committed title acknowledgement lost.");
            return Task.FromResult(new WonderlandTitleReceipt(added ? WonderlandTitleStatus.Applied : WonderlandTitleStatus.Duplicate,
                request.WorldInstanceId, request.Award, request.FrozenMembers.Select(member =>
                    new WonderlandTitleReceiptMember(member.AccountId, member.CharacterId, 100, 0, 8, true)).ToArray()));
        }
    }
}
