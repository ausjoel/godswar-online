using System.Text;
using System.Text.Json.Nodes;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Pets;
using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Infrastructure.Pets;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PetDurableCommandContractChecks
{
    public const string WonderlandSackContractCheckName =
        "Wonderland sack receipts preserve weighted evidence and historical bag contracts";

    public static async Task RunWonderlandSackContractsAsync()
    {
        CheckSackWeightedIntervals();
        var evidence = new WonderlandSackOpenEvidence(123, 4450, 25,
            WonderlandSackRewardPolicy.Revision, new string('A', 64),
            Roll: 0, TotalWeight: 82, OutcomeIndex: 0,
            RewardItemId: 10133, RewardQuantity: 25, RewardBound: 1);
        var receipt = SackReceipt(evidence);
        var bytes = PetDurablePersistenceCodec.Encode(receipt);
        var decoded = PetDurablePersistenceCodec.DecodeAndVerify(
            Encoding.UTF8.GetString(bytes), PetDurablePersistenceCodec.Hash(bytes));
        Check.True(PetDurablePersistenceCodec.ReadContractVersion(bytes) == 5 && decoded == receipt &&
            decoded.WonderlandSack == evidence,
            "the v5 receipt retains the exact source, policy, content revision, draw and selected reward");
        await new PetDurableOutboxConsumer().ConsumeAsync(CreateOutboxMessage(receipt, bytes));
        Check.Throws<InvalidDataException>(() =>
            new PetDurableOutboxConsumer().ConsumeAsync(CreateOutboxMessage(receipt, bytes, 4))
                .AsTask().GetAwaiter().GetResult(),
            "sack outbox delivery rejects a schema version that differs from the payload");

        var full = receipt with { Status = PetDurableReceiptStatus.WonderlandSackBagFull,
            WonderlandSack = null, OutboxEventId = null, AggregateRevision = 0 };
        var fullBytes = PetDurablePersistenceCodec.Encode(full);
        Check.True(!full.Succeeded && PetDurablePersistenceCodec.Decode(fullBytes) == full,
            "bag-full rejection carries no invented successful reward evidence");
        foreach (var invalid in new[]
        {
            receipt with { WonderlandSack = null },
            receipt with { KitBagSlot = 26 },
            receipt with { PetId = 1 },
            receipt with { WonderlandSack = evidence with { SourceItemInstanceId = 0 } },
            receipt with { WonderlandSack = evidence with { SourceTemplateId = 4174 } },
            receipt with { WonderlandSack = evidence with { Roll = 82 } },
            receipt with { WonderlandSack = evidence with { OutcomeIndex = 1 } },
            receipt with { WonderlandSack = evidence with { TotalWeight = 100 } },
            receipt with { WonderlandSack = evidence with { RewardItemId = 10134 } },
            receipt with { WonderlandSack = evidence with { RewardQuantity = 24 } },
            receipt with { WonderlandSack = evidence with { RewardBound = 2 } },
            receipt with { WonderlandSack = evidence with { ItemContentRevision = "not-a-revision" } },
            full with { WonderlandSack = evidence }
        })
            Check.Throws<InvalidDataException>(() => PetDurablePersistenceCodec.Encode(invalid),
                "invalid sack evidence cannot become a persisted receipt");

        // Real pre-v5 bag rejections lack sack evidence. Their original schemas
        // must remain readable without pretending they were new sack grants.
        var old = full with { Status = PetDurableReceiptStatus.UnsupportedItem };
        for (var version = 1; version <= 4; version++)
        {
            var document = JsonNode.Parse(PetDurablePersistenceCodec.Encode(old))!.AsObject();
            document["ContractVersion"] = version;
            document.Remove("WonderlandSack");
            document.Remove("PlayerExperience");
            if (version < 4) document.Remove("PlayerSkillLearn");
            if (version < 3) document.Remove("SkillLearn");
            if (version < 2) document.Remove("HatchRank");
            var legacy = Encoding.UTF8.GetBytes(document.ToJsonString());
            Check.True(PetDurablePersistenceCodec.DecodeAndVerify(Encoding.UTF8.GetString(legacy),
                PetDurablePersistenceCodec.Hash(legacy)) == old,
                $"historical bag contract v{version} replays with its original result");
        }
        var downgraded = JsonNode.Parse(bytes)!.AsObject();
        downgraded["ContractVersion"] = 4;
        Check.Throws<InvalidDataException>(() =>
            PetDurablePersistenceCodec.Decode(Encoding.UTF8.GetBytes(downgraded.ToJsonString())),
            "a successful sack grant cannot downgrade into a schema that discards its evidence");
    }

    private static void CheckSackWeightedIntervals()
    {
        foreach (var id in Enumerable.Range(4450, 12).Select(id => (uint)id))
        {
            WonderlandSackReward[] expected = id switch
            {
                <= 4455 => [new(10133, 25, 10), new(4174, 25, 15), new(10133, 10, 25),
                    new(10133, 5, 30), new(10107, 1, 2)],
                <= 4460 => [new(10134, 10, 20), new(10133, 50, 20), new(10134, 20, 10), new(10107, 1, 10)],
                _ => [new(10134, 99, 50), new(10107, 3, 50), new(4213, 1, 50), new(4223, 1, 50)]
            };
            var total = expected.Sum(row => row.Weight);
            Check.True(WonderlandSackRewardPolicy.Outcomes(id).SequenceEqual(expected) &&
                WonderlandSackRewardPolicy.TotalWeight(id) == total,
                $"sack {id} uses the requested early, late or Scorpion reward group");
            var observed = new int[expected.Length];
            for (var roll = 0; roll < total; roll++)
            {
                var selected = WonderlandSackRewardPolicy.SelectIndex(id, roll);
                Check.True(selected >= 0 && selected < expected.Length &&
                    expected[selected].ItemId > 0 && expected[selected].Quantity > 0,
                    "each possible roll selects exactly one nonempty reward");
                observed[selected]++;
            }
            Check.True(observed.SequenceEqual(expected.Select(row => row.Weight)),
                $"every sack {id} outcome receives exactly its requested relative weight");
            Check.Throws<ArgumentOutOfRangeException>(() => WonderlandSackRewardPolicy.SelectIndex(id, -1),
                "negative sack rolls are rejected");
            Check.Throws<ArgumentOutOfRangeException>(() => WonderlandSackRewardPolicy.SelectIndex(id, total),
                "the upper roll bound is exclusive");
        }
        Check.Throws<ArgumentOutOfRangeException>(() => WonderlandSackRewardPolicy.Outcomes(4449),
            "an unrelated item below the sack family has no reward table");
        Check.Throws<ArgumentOutOfRangeException>(() => WonderlandSackRewardPolicy.Outcomes(4462),
            "an unrelated item above the sack family has no reward table");
    }

    private static PetDurableReceipt SackReceipt(WonderlandSackOpenEvidence evidence) =>
        new(CommandFamily.BagItemActivation, PetDurableReceiptStatus.WonderlandSackOpened,
            AccountId: 13, CharacterId: 2, KitBagSlot: evidence.KitBagSlot,
            EquipmentSlot: -1, PetId: 0, PetLevel: 0, PetExperience: 0, PetRevision: 0,
            IsCarried: false, IsSummoned: false, PresenceOperation: 0, AggregateRevision: 1,
            AuditReference: "wonderland-sack-contract-check", OutboxEventId: Guid.NewGuid(),
            WonderlandSack: evidence);
}
