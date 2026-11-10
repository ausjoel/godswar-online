using System.Text;
using System.Text.Json.Nodes;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Pets;
using Godswar.Server.Infrastructure.Pets;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PetDurableCommandContractChecks
{
    public const string PlayerExperienceContractCheckName =
        "EXP Pill receipts prove character progression and reject lost or altered evidence";

    public static async Task RunPlayerExperienceContractsAsync()
    {
        var evidence = PillEvidence();
        var receipt = PillReceipt(evidence);
        var payload = PetDurablePersistenceCodec.Encode(receipt);
        Check.True(PetDurablePersistenceCodec.ReadContractVersion(payload) == 5 &&
            PetDurablePersistenceCodec.DecodeAndVerify(Encoding.UTF8.GetString(payload),
                PetDurablePersistenceCodec.Hash(payload)) == receipt,
            "v5 retains the source item, exact million EXP, seal, level transition and progression revisions");
        await new PetDurableOutboxConsumer().ConsumeAsync(CreateOutboxMessage(receipt, payload));
        Check.Throws<InvalidDataException>(() => new PetDurableOutboxConsumer()
            .ConsumeAsync(CreateOutboxMessage(receipt, payload, 4)).AsTask().GetAwaiter().GetResult(),
            "pill outbox delivery rejects a downgraded schema");

        PlayerExperienceItemEvidence[] malformed =
        [
            evidence with { ItemInstanceId = 0 },
            evidence with { KitBagSlot = -1 },
            evidence with { KitBagSlot = 96 },
            evidence with { ItemTemplateId = 4173 },
            evidence with { ExperienceGranted = 999_999 },
            evidence with { ExperienceGranted = 1_000_001 },
            evidence with { PreviousLevel = 0 },
            evidence with { PreviousLevel = 201 },
            evidence with { PreviousExperience = -1 },
            evidence with { PreviousExperience = PlayerExperienceCatalog.MaximumStoredExperience + 1 },
            evidence with { FighterLevelSealed = true },
            evidence with { NewLevel = evidence.NewLevel + 1 },
            evidence with { NewExperience = evidence.NewExperience + 1 },
            evidence with { PreviousProgressionRevision = -1 },
            evidence with { PreviousProgressionRevision = long.MaxValue },
            evidence with { NewProgressionRevision = evidence.PreviousProgressionRevision },
            evidence with { NewProgressionRevision = evidence.NewProgressionRevision + 1 }
        ];
        foreach (var invalid in malformed)
        {
            Check.True(!invalid.IsValid, "a malformed pill proof fails its semantic validation");
            Check.Throws<InvalidDataException>(() => PetDurablePersistenceCodec.Encode(
                receipt with { PlayerExperience = invalid }),
                "invalid pill progression evidence cannot be persisted");
        }
        foreach (var invalid in new[]
        {
            receipt with { PlayerExperience = null },
            receipt with { KitBagSlot = 26 },
            receipt with { EquipmentSlot = 1 },
            receipt with { PetId = 1 },
            receipt with { PetRevision = 1 },
            receipt with { Family = CommandFamily.PetLevelUpgrade },
            receipt with { OutboxEventId = null },
            receipt with { Status = PetDurableReceiptStatus.PlayerExperienceMaximumReached },
            receipt with { Status = PetDurableReceiptStatus.ConsumableCooldownActive }
        })
            Check.Throws<InvalidDataException>(() => PetDurablePersistenceCodec.Encode(invalid),
                "pill success cannot lose its evidence or masquerade as a rejection or pet operation");

        foreach (var status in new[] { PetDurableReceiptStatus.PlayerExperienceMaximumReached,
                     PetDurableReceiptStatus.ConsumableCooldownActive })
        {
            var rejected = receipt with { Status = status, PlayerExperience = null,
                OutboxEventId = null, AggregateRevision = 0 };
            Check.True(!rejected.Succeeded &&
                PetDurablePersistenceCodec.Decode(PetDurablePersistenceCodec.Encode(rejected)) == rejected,
                "cap and cooldown receipts retain the bag slot without invented EXP evidence");
        }

        var modified = JsonNode.Parse(payload)!.AsObject();
        modified["PlayerExperience"]!["NewExperience"] = evidence.NewExperience + 1;
        Check.Throws<InvalidDataException>(() => PetDurablePersistenceCodec.Decode(
            Encoding.UTF8.GetBytes(modified.ToJsonString())),
            "decoding independently validates the recorded progression transition");
        modified = JsonNode.Parse(payload)!.AsObject();
        modified["AuditReference"] = "tampered-audit";
        Check.Throws<InvalidDataException>(() => PetDurablePersistenceCodec.DecodeAndVerify(
            modified.ToJsonString(), PetDurablePersistenceCodec.Hash(payload)),
            "even semantically valid payload changes must match the persisted receipt hash");
        for (var version = 1; version <= 4; version++)
        {
            modified = JsonNode.Parse(payload)!.AsObject();
            modified["ContractVersion"] = version;
            Check.Throws<InvalidDataException>(() => PetDurablePersistenceCodec.Decode(
                Encoding.UTF8.GetBytes(modified.ToJsonString())),
                $"a v{version} decoder must reject a pill success whose evidence would be discarded");
        }
    }

    private static PlayerExperienceItemEvidence PillEvidence()
    {
        Check.True(PlayerExperienceItemPolicy.TryApply(10, 123, false, out var result),
            "the sample pill has a valid character progression transition");
        Check.True(result.Level > 10, "the sample proof exercises a real level transition");
        return new(123, 25, 4174, 1_000_000, 10, 123, false,
            result.Level, result.Experience, 7, 8);
    }

    private static PetDurableReceipt PillReceipt(PlayerExperienceItemEvidence evidence) =>
        new(CommandFamily.BagItemActivation, PetDurableReceiptStatus.PlayerExperienceAdded,
            AccountId: 13, CharacterId: 2, KitBagSlot: evidence.KitBagSlot,
            EquipmentSlot: -1, PetId: 0, PetLevel: 0, PetExperience: 0, PetRevision: 0,
            IsCarried: false, IsSummoned: false, PresenceOperation: 0, AggregateRevision: 1,
            AuditReference: "player-experience-contract-check", OutboxEventId: Guid.NewGuid(),
            PlayerExperience: evidence);
}
