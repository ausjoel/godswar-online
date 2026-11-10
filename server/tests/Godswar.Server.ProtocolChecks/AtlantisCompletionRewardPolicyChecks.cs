using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Domain.World.Instances;

namespace Godswar.Server.ProtocolChecks;

internal static class AtlantisCompletionRewardPolicyChecks
{
    public const string CheckName = "Atlantis completion rewards preserve original admission and eligible finishers";

    public static Task RunAsync()
    {
        var instance = WorldInstanceId.New();
        var reservation = Guid.NewGuid();
        var start = new DateTimeOffset(2026, 9, 8, 0, 0, 0, TimeSpan.Zero);
        AtlantisCompletionMember[] admitted = [new(11, 101), new(12, 102)];
        var party = new AtlantisCompletionRewardRequest(instance, RealmId.Tempest, reservation,
            start, start.AddMinutes(20), 850, admitted, [admitted[0]]);
        Check.True(party.Award == new AtlantisCompletionRewardAward(2800, 5013, "Seabed Explorer") &&
            party.AdmittedMembers.Count == 2 && party.FrozenMembers.Count == 1,
            "one remaining finisher receives a party title while voluntary leavers are excluded");
        var solo = new AtlantisCompletionRewardRequest(WorldInstanceId.New(), RealmId.Tempest, Guid.NewGuid(),
            start, start.AddMinutes(1), 850, [admitted[0]], [admitted[0]]);
        Check.True(solo.Award == new AtlantisCompletionRewardAward(2800, 5014, "Deep Sea Hunter"),
            "actual solo admission grants the stock Deep Sea Hunter title and documented2800 HardPoints");
        var slow = new AtlantisCompletionRewardRequest(WorldInstanceId.New(), RealmId.Tempest, Guid.NewGuid(),
            start, start.AddMinutes(40).AddTicks(-1), 850, [admitted[0]], [admitted[0]]);
        Check.Equal(solo.Award, slow.Award, "completion time does not invent an undocumented HardPoints multiplier");
        var ordered = new AtlantisCompletionRewardRequest(instance, RealmId.Tempest, reservation,
            start, start.AddMinutes(20), 850, admitted.Reverse().ToArray(), [admitted[0]]);
        Check.Equal(party.RequestHash, ordered.RequestHash, "canonical evidence is independent of roster enumeration order");
        var changedFinisher = new AtlantisCompletionRewardRequest(instance, RealmId.Tempest, reservation,
            start, start.AddMinutes(20), 850, admitted, [admitted[1]]);
        Check.True(changedFinisher.RequestHash != party.RequestHash, "eligible finishers are bound into durable identity");
        admitted[0] = new(99, 999);
        Check.True(party.FrozenMembers[0] == new AtlantisCompletionMember(11, 101) && party.AdmittedMembers[0].CharacterId == 101,
            "caller mutations cannot change captured completion or original admission evidence");

        // A member who confirmed the party window after the run was sealed holds
        // their own admission reservation, so entitlement is presence at
        // completion while the award keeps the registered party's classification.
        var withLateMember = new AtlantisCompletionRewardRequest(instance, RealmId.Tempest, reservation,
            start, start.AddMinutes(20), 850, [new AtlantisCompletionMember(11, 101)],
            [new AtlantisCompletionMember(11, 101), new AtlantisCompletionMember(13, 103)]);
        Check.True(withLateMember.Award == new AtlantisCompletionRewardAward(2800, 5014, "Deep Sea Hunter") &&
            withLateMember.AdmittedCharacterIds.Count == 1 &&
            withLateMember.CharacterIds.SequenceEqual(new[] { 101, 103 }),
            "a later member is entitled by presence while the award keeps the registered party's classification");

        void Invalid(int score, TimeSpan duration, AtlantisCompletionMember[] original, AtlantisCompletionMember[] finishers) =>
            Check.Throws<ArgumentException>(() => new AtlantisCompletionRewardRequest(instance, RealmId.Tempest,
                reservation, start, start.Add(duration), score, original, finishers), "invalid Atlantis entitlement rejects before storage");
        AtlantisCompletionMember[] one = [new(11, 101)];
        // A run that ends before the completion threshold still settles, paying the
        // published incomplete tier for its team points - the highest tier at or
        // below the score ("floor"): 63 pays the 50 tier, 720 the 700 tier - and
        // never a title.
        (int Score, int HardPoints)[] incompleteTiers =
        [
            (0, 200), (50, 600), (63, 600), (80, 900), (100, 1_000), (150, 1_200),
            (180, 1_300), (220, 1_420), (250, 1_540), (350, 1_660), (400, 1_820),
            (500, 2_000), (600, 2_200), (700, 2_400), (720, 2_400), (849, 2_400)
        ];
        Check.True(
            incompleteTiers.All(tier =>
                AtlantisCompletionRewardPolicy.Resolve(tier.Score, 2).HardPoints == tier.HardPoints &&
                AtlantisCompletionRewardPolicy.Resolve(tier.Score, 2).TitleId == 0 &&
                new AtlantisCompletionRewardRequest(instance, RealmId.Tempest, reservation, start,
                    start.AddMinutes(20), tier.Score, [new(11, 101), new(12, 102)],
                    [new(11, 101), new(12, 102)]).Award.HardPoints == tier.HardPoints),
            "every published incomplete tier pays its HardPoints with no title at its floor score");
        Check.True(
            AtlantisCompletionRewardPolicy.Resolve(850, 1).TitleId ==
                AtlantisCompletionRewardPolicy.DeepSeaHunterTitleId &&
            AtlantisCompletionRewardPolicy.Resolve(850, 2).TitleId ==
                AtlantisCompletionRewardPolicy.SeabedExplorerTitleId,
            "the completion threshold still grants the solo and party titles by roster size");
        // A run that reaches its own forty-minute deadline terminalizes exactly on
        // it, so that evidence is valid ("timed out"); only a run that outlived its
        // limit is rejected.
        Check.True(
            new AtlantisCompletionRewardRequest(instance, RealmId.Tempest, reservation, start,
                start.AddMinutes(40), 700, one, one).Award.HardPoints == 2_400,
            "a run that timed out on its deadline still pays the tier its score reached");
        Invalid(851, TimeSpan.FromMinutes(1), one, one);
        Invalid(850, TimeSpan.FromMinutes(40).Add(TimeSpan.FromTicks(1)), one, one);
        Invalid(850, TimeSpan.FromTicks(-1), one, one);
        Invalid(850, TimeSpan.FromMinutes(1), one, []);
        Invalid(850, TimeSpan.FromMinutes(1), one, [new(11, 101), new(11, 102)]);
        Invalid(850, TimeSpan.FromMinutes(1), [one[0], one[0]], one);
        Invalid(850, TimeSpan.FromMinutes(1), Enumerable.Range(1, 6).Select(id => new AtlantisCompletionMember(id, id)).ToArray(), one);
        return Task.CompletedTask;
    }
}
