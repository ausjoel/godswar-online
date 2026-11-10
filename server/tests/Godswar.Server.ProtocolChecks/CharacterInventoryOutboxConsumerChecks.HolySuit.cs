using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Inventory;
using Godswar.Server.Application.Messaging;
using Godswar.Server.Infrastructure.Inventory;

namespace Godswar.Server.ProtocolChecks;

internal static partial class CharacterInventoryOutboxConsumerChecks
{
    private static async Task CheckHolySuitAsync(
        CharacterInventoryOutboxConsumer consumer)
    {
        var messages = Enum.GetValues<HolySuitCommandOperation>()
            .Select((operation, index) => CreateHolySuitMessage(
                operation,
                revision: 100 + index))
            .ToArray();
        foreach (var message in messages)
        {
            Check.True(
                HolySuitPersistenceCodec.IsEventType(message.EventType),
                $"{message.EventType} is an exact Holy Suit event type");
            await consumer.ConsumeAsync(message);
        }
        Check.True(
            !HolySuitPersistenceCodec.IsEventType(
                "inventory.holy_suit_unknown"),
            "unknown Holy Suit event type stays unsupported");

        var store = messages[0];
        await CheckThrowsAsync<InvalidDataException>(
            () => consumer.ConsumeAsync(CreateMessage(
                Guid.NewGuid(),
                store.AggregateRevision,
                store.EventType,
                store.SchemaVersion,
                store.Payload)).AsTask(),
            "Holy Suit event ID mismatch is rejected");
        await CheckThrowsAsync<InvalidDataException>(
            () => consumer.ConsumeAsync(CreateMessage(
                store.EventId,
                store.AggregateRevision + 1,
                store.EventType,
                store.SchemaVersion,
                store.Payload)).AsTask(),
            "Holy Suit inventory revision mismatch is rejected");
        await CheckThrowsAsync<InvalidDataException>(
            () => consumer.ConsumeAsync(CreateMessage(
                store.EventId,
                store.AggregateRevision,
                store.EventType,
                store.SchemaVersion,
                store.Payload,
                "character:999:inventory")).AsTask(),
            "Holy Suit aggregate key mismatch is rejected");
        await CheckThrowsAsync<InvalidDataException>(
            () => consumer.ConsumeAsync(CreateMessage(
                store.EventId,
                store.AggregateRevision,
                HolySuitPersistenceCodec.EventType(
                    CommandFamily.HolySuitTransferExperience),
                store.SchemaVersion,
                store.Payload)).AsTask(),
            "Holy Suit payload family and event type must agree");
        await CheckThrowsAsync<InvalidDataException>(
            () => consumer.ConsumeAsync(CreateMessage(
                store.EventId,
                store.AggregateRevision,
                store.EventType,
                HolySuitPersistenceCodec.ContractVersion + 1,
                store.Payload)).AsTask(),
            "unsupported Holy Suit schema is rejected");
        await CheckThrowsAsync<InvalidDataException>(
            () => consumer.ConsumeAsync(CreateMessage(
                store.EventId,
                store.AggregateRevision,
                "inventory.holy_suit_unknown",
                store.SchemaVersion,
                store.Payload)).AsTask(),
            "unsupported Holy Suit event is rejected");
    }

    private static OutboxEventMessage CreateHolySuitMessage(
        HolySuitCommandOperation operation,
        long revision)
    {
        var eventId = Guid.NewGuid();
        var receipt = CreateHolySuitReceipt(operation, revision, eventId);
        return CreateMessage(
            eventId,
            revision,
            HolySuitPersistenceCodec.EventType(receipt.Family),
            HolySuitPersistenceCodec.ContractVersion,
            HolySuitPersistenceCodec.Encode(receipt));
    }

    private static HolySuitExecutionReceipt CreateHolySuitReceipt(
        HolySuitCommandOperation operation,
        long revision,
        Guid eventId)
    {
        var status = operation switch
        {
            HolySuitCommandOperation.StoreExperience =>
                HolySuitCommandResultStatus.ExperienceStored,
            HolySuitCommandOperation.TransferExperience =>
                HolySuitCommandResultStatus.ExperienceTransferred,
            HolySuitCommandOperation.ConsumeWare =>
                HolySuitCommandResultStatus.WareConsumed,
            HolySuitCommandOperation.TransformExperience =>
                HolySuitCommandResultStatus.ExperienceTransformed,
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        };
        var requestedExperience = operation ==
            HolySuitCommandOperation.StoreExperience
                ? 100_000L
                : 0L;
        var requestedPrisms = operation ==
            HolySuitCommandOperation.TransformExperience
                ? 2
                : 0;
        var experienceBefore = operation ==
            HolySuitCommandOperation.TransformExperience
                ? 300_000_000L
                : 500_000_000L;
        var experienceAfter = operation switch
        {
            HolySuitCommandOperation.StoreExperience => 499_900_000L,
            HolySuitCommandOperation.TransformExperience => 100_000_000L,
            _ => experienceBefore
        };
        var dailyAfter = operation ==
            HolySuitCommandOperation.StoreExperience
                ? 100_000L
                : 0L;
        HolySuitReceiptMutation[] mutations = operation switch
        {
            HolySuitCommandOperation.StoreExperience =>
            [
                HolySuitMutation(
                    HolySuitReceiptItemRole.HolyBox,
                    1, 9024, 101, "[9024,0]", "[9024,100000]")
            ],
            HolySuitCommandOperation.TransferExperience =>
            [
                HolySuitMutation(
                    HolySuitReceiptItemRole.Equipment,
                    1, 1100, 102, "[1100,0]", "[1100,100000]"),
                HolySuitMutation(
                    HolySuitReceiptItemRole.HolyBox,
                    2, 9024, 103, "[9024,100000]", "[]")
            ],
            HolySuitCommandOperation.ConsumeWare =>
            [
                HolySuitMutation(
                    HolySuitReceiptItemRole.Equipment,
                    1, 1100, 104, "[1100,0]", "[1100,1]"),
                HolySuitMutation(
                    HolySuitReceiptItemRole.Ware,
                    2, 9010, 105, "[9010,2]", "[9010,1]")
            ],
            HolySuitCommandOperation.TransformExperience =>
            [
                HolySuitMutation(
                    HolySuitReceiptItemRole.ExperiencePrism,
                    3, 9025, 106, "[]", "[9025,2]")
            ],
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        };
        return new HolySuitExecutionReceipt(
            CharacterId,
            operation,
            HolySuitCommandEnvelope.SpartaNpcId,
            HolySuitCommandEnvelope.DialogIndex,
            status,
            HolySuitNativeResults.GetResultSubId(operation, status),
            requestedExperience,
            requestedPrisms,
            experienceBefore,
            experienceAfter,
            dailyStoredExperienceBefore: 0,
            dailyAfter,
            battlePassDailyLimitExempt: false,
            prismsCreated: requestedPrisms,
            prismsConsumed: 0,
            mutations,
            progressionRevision: revision,
            inventoryRevision: revision,
            auditReference: $"holy-suit-outbox-{revision}",
            outboxEventId: eventId);
    }

    private static HolySuitReceiptMutation HolySuitMutation(
        HolySuitReceiptItemRole role,
        int slot,
        uint itemId,
        long instanceId,
        string before,
        string after) =>
        new(role, slot, itemId, instanceId, before, after);
}
