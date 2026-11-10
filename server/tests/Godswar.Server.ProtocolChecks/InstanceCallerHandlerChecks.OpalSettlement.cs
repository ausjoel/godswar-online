using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Packets;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class InstanceCallerHandlerChecks
{
    private static async Task CheckAtlantisPaidPayerProjectionAsync()
    {
        var daily = new ScriptedLegacyInstanceDailyEntryStore
        {
            PaymentRequiredIndexes = new HashSet<int> { 0, 2 }
        };
        var payments = new ScriptedLegacyInstanceOpalPaymentStore();
        await using var fixture = await CreateAtlantisOpalFixtureAsync(
            daily,
            payments);
        InstallOpals(fixture);
        var originalBags = fixture.Characters
            .Select(static character => character.KitBag)
            .ToArray();
        var originalVitals = fixture.Characters
            .Select(static character =>
                (character.CurrentHp, character.CurrentMp))
            .ToArray();
        var packetOffsets = fixture.ReadAllPackets()
            .Select(static packets => packets.Count)
            .ToArray();
        payments.MutationFactory = request => request.Payers
            .Select(payer => CreateOpalDecrementMutation(
                fixture.Characters.Single(character =>
                    character.Id == payer.CharacterId)))
            .ToArray();

        await EnterAtlantisAsync(
            fixture,
            InstanceCallerProtocol.AtlantisOpalSubId);

        var expectedPayers = new[]
        {
            fixture.Characters[0].Id,
            fixture.Characters[2].Id
        };
        var actualPayers = payments.Charges.Single().Payers
            .Select(static payer => payer.CharacterId)
            .ToArray();
        var packets = fixture.ReadAllPackets();
        Check.True(
            actualPayers.SequenceEqual(expectedPayers) &&
            LegacyInstanceOpalPaymentPolicy.OpalsPerRetryingCharacter == 1 &&
            payments.Charges[0].Payers.All(payer =>
                payer.Ownership.IsValid) &&
            payments.Settlements.Single().AdmittedCharacterIds.SetEquals(
                fixture.Characters.Select(static character => character.Id)) &&
            payments.Admissions
                .SelectMany(static admission => admission.CharacterIds)
                .ToHashSet()
                .SetEquals(fixture.Characters.Select(static x => x.Id)) &&
            daily.Admissions
                .SelectMany(static admission => admission.CharacterIds)
                .ToHashSet()
                .SetEquals(fixture.Characters.Select(static x => x.Id)) &&
            daily.FullReleases.Count == 0 &&
            daily.MemberReleases.Count == 0 &&
            fixture.Characters.All(character =>
                character.CurrentMap == 205) &&
            AllSessionsShareCurrentInstance(fixture),
            "paid Atlantis retry charges exactly the returning characters " +
            "and settles all successfully admitted party members");

        for (var index = 0; index < fixture.Characters.Count; index++)
        {
            var character = fixture.Characters[index];
            var isPayer = index is 0 or 2;
            var expectedBag = isPayer
                ? KitBagSlots.SetSlot(
                    originalBags[index],
                    AtlantisOpalSlot,
                    (CompactItemEntry.Parse(KitBagSlots.GetEntry(
                        originalBags[index],
                        AtlantisOpalSlot)) with { Stack = 1 })
                        .ToCompactString())
                : originalBags[index];
            var emitted = packets[index]
                .Skip(packetOffsets[index])
                .ToArray();
            Check.True(
                character.KitBag == expectedBag &&
                character.CurrentHp == originalVitals[index].CurrentHp &&
                character.CurrentMp == originalVitals[index].CurrentMp &&
                emitted.All(packet => ReadOpcode(packet) is
                    not 0x273B and not 0x2771) &&
                emitted.Any(packet => ReadOpcode(packet) == 0x2731) ==
                    isPayer,
                $"Atlantis Opal projection updates only payer {index}'s " +
                "bag and emits no HP/MP refresh");
        }
    }

    private static async Task CheckAtlantisFailedLeaderSettlementAsync()
    {
        var daily = ReturningPartyDailyEntries();
        var payments = new ScriptedLegacyInstanceOpalPaymentStore();
        await using var fixture = await CreateAtlantisOpalFixtureAsync(
            daily,
            payments);
        var sourceInstance = GetSourceInstanceId(fixture.Leader);
        foreach (var follower in fixture.Followers)
        {
            await ConsentAtlantisRetryAsync(follower);
        }
        await OpenAtlantisPageAsync(fixture.Leader);
        SetHandlerField(fixture.Leader.Handler, "_registered", false);
        var before = fixture.Leader.ReadPackets().Count;

        await InvokeAsync(
            fixture.Leader.Handler,
            CreateActionPacket(
                InstanceCallerProtocol.AtlantisRootSubId,
                InstanceCallerProtocol.AtlantisOpalSubId));
        var emitted = ReadAdmissionPackets(fixture.Leader.ReadPackets().Skip(before));

        Check.True(
            daily.Claims.Count == 1 &&
            daily.FullReleases.Count == 0 &&
            daily.MemberReleases.Count == 0 &&
            payments.Charges.Count == 1 &&
            payments.Admissions.Count == 0 &&
            payments.Settlements is [var settlement] &&
            settlement.ReservationId == daily.Claims[0].ReservationId &&
            settlement.AdmittedCharacterIds.Count == 0 &&
            AllSessionsRemainAtSource(fixture, sourceInstance) &&
            emitted.Single().SequenceEqual(PacketBuilder.ServerNote(
                "The instance is temporarily unavailable.")),
            "a failed paid leader transfer gives the durable payment store " +
            "an empty admitted settlement so it refunds every Opal and " +
            "releases every attempt");
    }

    private static async Task CheckAtlantisFailedFollowerSettlementAsync()
    {
        var daily = ReturningPartyDailyEntries();
        var payments = new ScriptedLegacyInstanceOpalPaymentStore();
        await using var fixture = await CreateAtlantisOpalFixtureAsync(
            daily,
            payments,
            failedFollowerIndexes: new HashSet<int> { 2 });
        var sourceInstance = GetSourceInstanceId(fixture.Leader);

        var emitted = await EnterAtlantisAsync(
            fixture,
            InstanceCallerProtocol.AtlantisOpalSubId);

        var failedId = fixture.Characters[2].Id;
        var admittedIds = fixture.Characters
            .Take(2)
            .Select(static character => character.Id)
            .ToHashSet();
        Check.True(
            daily.Claims.Count == 1 &&
            daily.FullReleases.Count == 0 &&
            daily.MemberReleases.Count == 0 &&
            payments.Charges.Count == 1 &&
            payments.Admissions
                .SelectMany(static admission => admission.CharacterIds)
                .ToHashSet()
                .SetEquals(admittedIds) &&
            daily.Admissions
                .SelectMany(static admission => admission.CharacterIds)
                .ToHashSet()
                .SetEquals(admittedIds) &&
            payments.Settlements is [var settlement] &&
            settlement.ReservationId == daily.Claims[0].ReservationId &&
            settlement.AdmittedCharacterIds.SetEquals(admittedIds) &&
            fixture.Characters[0].CurrentMap == 205 &&
            fixture.Characters[1].CurrentMap == 205 &&
            fixture.Characters[2].CurrentMap ==
                fixture.Leader.SourceMapId &&
            fixture.Leader.Registry.TryGetSessionWorldInstanceId(
                fixture.Sessions[2],
                out var failedInstance) &&
            failedInstance == sourceInstance &&
            emitted.Any(packet => packet.SequenceEqual(
                PacketBuilder.ServerNote(
                    "The instance is temporarily unavailable."))),
            "a failed paid follower transfer releases and refunds only that " +
            "member while retaining the two admitted attempts and charges");
    }

    private static LegacyInstanceOpalInventoryMutation
        CreateOpalDecrementMutation(GameCharacter character)
    {
        var before = KitBagSlots.GetEntry(
            character.KitBag,
            AtlantisOpalSlot);
        var item = CompactItemEntry.Parse(before);
        Check.True(
            item.Id == LegacyInstanceOpalPaymentPolicy.OpalItemTemplateId &&
            item.Stack == 2,
            "Opal projection fixture begins with a two-item stack");
        return new LegacyInstanceOpalInventoryMutation(
            character.AccountId,
            character.Id,
            AtlantisOpalSlot,
            before,
            (item with { Stack = 1 }).ToCompactString());
    }
}
