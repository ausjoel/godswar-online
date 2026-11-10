using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Pets;
using Godswar.Server.Infrastructure.Pets;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PetDurableCommandContractChecks
{
    private static void CheckReceiptRoundTrip()
    {
        var receipt = new PetDurableReceipt(
            CommandFamily.PetPresenceTransition,
            PetDurableReceiptStatus.PresenceChanged,
            13,
            2,
            -1,
            -1,
            71,
            4,
            12_345,
            9,
            true,
            true,
            PresenceOperation: 2,
            AggregateRevision: 5,
            AuditReference: "42",
            OutboxEventId: Guid.NewGuid());
        var payload = PetDurablePersistenceCodec.Encode(receipt);
        var decoded = PetDurablePersistenceCodec.DecodeAndVerify(
            System.Text.Encoding.UTF8.GetString(payload),
            PetDurablePersistenceCodec.Hash(payload));
        Check.Equal(receipt, decoded, "pet receipt canonical round trip");
        Check.Throws<InvalidDataException>(
            () => PetDurablePersistenceCodec.DecodeAndVerify(
                System.Text.Encoding.UTF8.GetString(payload),
                new byte[32]),
            "pet receipt rejects a forged result hash");
    }

    private static void CheckLegacyPetGrowthReceipt()
    {
        var outboxEventId = Guid.NewGuid();
        var payload = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(
            new
            {
                ContractVersion =
                    PetDurablePersistenceCodec.ContractVersion,
                Family = (ushort)CommandFamily.PetGrowthReset,
                Status = (byte)PetDurableReceiptStatus.PetGrowthReset,
                AccountId = 13,
                CharacterId = 2,
                KitBagSlot = 7,
                EquipmentSlot = -1,
                PetId = 71L,
                PetLevel = (short)20,
                PetExperience = 12_345L,
                PetRevision = 9L,
                IsCarried = true,
                IsSummoned = true,
                PresenceOperation = (byte)0,
                AggregateRevision = 5L,
                AuditReference = "legacy-growth-reset",
                OutboxEventId = (Guid?)outboxEventId
            });
        var decoded = PetDurablePersistenceCodec.DecodeAndVerify(
            System.Text.Encoding.UTF8.GetString(payload),
            PetDurablePersistenceCodec.Hash(payload));

        Check.True(
            decoded.Family == CommandFamily.PetGrowthReset &&
            decoded.Status == PetDurableReceiptStatus.PetGrowthReset &&
            decoded.GrowthPreview is null &&
            decoded.OutboxEventId == outboxEventId,
            "legacy Growth-reset receipt remains hash-compatible");
    }

    private static void CheckPetShedReceipts()
    {
        var expanded = new PetDurableReceipt(
            CommandFamily.BagItemActivation,
            PetDurableReceiptStatus.PetShedExpanded,
            AccountId: 13,
            CharacterId: 2,
            KitBagSlot: 25,
            EquipmentSlot: -1,
            PetId: 0,
            PetLevel: 0,
            PetExperience: 0,
            PetRevision: 0,
            IsCarried: false,
            IsSummoned: false,
            PresenceOperation: 0,
            AggregateRevision: 6,
            AuditReference: "shed-expanded",
            OutboxEventId: Guid.NewGuid());
        var payload = PetDurablePersistenceCodec.Encode(expanded);
        Check.Equal(
            expanded,
            PetDurablePersistenceCodec.DecodeAndVerify(
                System.Text.Encoding.UTF8.GetString(payload),
                PetDurablePersistenceCodec.Hash(payload)),
            "pet shed expansion receipt has a canonical durable round trip");
        Check.True(expanded.Succeeded, "pet shed expansion is successful");

        var maximum = expanded with
        {
            Status = PetDurableReceiptStatus.PetShedMaximumReached,
            AggregateRevision = 5,
            AuditReference = "shed-maximum",
            OutboxEventId = null
        };
        maximum.Validate();
        Check.True(
            !maximum.Succeeded,
            "maximum pet shed is a non-consuming terminal rejection");
        Check.Throws<InvalidDataException>(
            () => (maximum with { OutboxEventId = Guid.NewGuid() }).Validate(),
            "maximum pet shed cannot claim a committed outbox event");
    }

    private static void CheckMigration()
    {
        var migration = PostgresSchemaMigrationCatalog.All.Single(
            candidate => string.Equals(
                candidate.Id,
                "20260731_034_pet_durability_foundation",
                StringComparison.Ordinal));
        Check.True(
            migration.Sql.Contains(
                "CREATE TABLE public.pet_durable_stream_versions",
                StringComparison.Ordinal),
            "pet durability migration owns a bounded stream");
        Check.True(
            migration.Sql.Contains(
                "ON DELETE CASCADE",
                StringComparison.Ordinal),
            "pet stream projection cannot block controlled character purge");
        Check.True(
            migration.Sql.Contains(
                "character_pet_value",
                StringComparison.Ordinal),
            "pet evidence view scopes durable commands");
        Check.True(
            migration.Sql.Contains(
                "event.consumer_key = 'pet_durable_v1'",
                StringComparison.Ordinal),
            "pet evidence view excludes its coupled inventory event");
    }
}
