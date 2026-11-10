using Godswar.Server.Application.Messaging;
using Godswar.Server.Infrastructure.Inventory;

namespace Godswar.Server.ProtocolChecks;

internal static partial class CharacterInventoryOutboxConsumerChecks
{
    private static async Task CheckIdentityRejectionAsync(
        CharacterInventoryOutboxConsumer consumer,
        OutboxEventMessage bagClear)
    {
        var inconsistent = CreateMessage(
            bagClear.EventId,
            bagClear.AggregateRevision,
            bagClear.EventType,
            bagClear.SchemaVersion,
            bagClear.Payload,
            aggregateKey: "character:999:inventory");
        await CheckThrowsAsync<InvalidDataException>(
            () => consumer.ConsumeAsync(inconsistent).AsTask(),
            "bag-clear payload identity mismatch is rejected");
    }

    private static async Task CheckContractRejectionAsync(
        CharacterInventoryOutboxConsumer consumer,
        OutboxEventMessage legacyGrant)
    {
        var unsupported = CreateMessage(
            legacyGrant.EventId,
            legacyGrant.AggregateRevision,
            eventType: "inventory.unsupported",
            legacyGrant.SchemaVersion,
            legacyGrant.Payload);
        await CheckThrowsAsync<InvalidDataException>(
            () => consumer.ConsumeAsync(unsupported).AsTask(),
            "unknown inventory event type is rejected");
    }

    private static async Task CheckStoneIdentityRejectionAsync(
        CharacterInventoryOutboxConsumer consumer,
        OutboxEventMessage makeAttributeStone)
    {
        var inconsistent = CreateMessage(
            Guid.NewGuid(),
            makeAttributeStone.AggregateRevision,
            makeAttributeStone.EventType,
            makeAttributeStone.SchemaVersion,
            makeAttributeStone.Payload);
        await CheckThrowsAsync<InvalidDataException>(
            () => consumer.ConsumeAsync(inconsistent).AsTask(),
            "Make Attribute Stone event identity mismatch is rejected");
    }

    private static async Task CheckStoneContractRejectionAsync(
        CharacterInventoryOutboxConsumer consumer,
        OutboxEventMessage makeAttributeStone)
    {
        var unsupported = CreateMessage(
            makeAttributeStone.EventId,
            makeAttributeStone.AggregateRevision,
            makeAttributeStone.EventType,
            MakeAttributeStonePersistenceCodec.ContractVersion + 1,
            makeAttributeStone.Payload);
        await CheckThrowsAsync<InvalidDataException>(
            () => consumer.ConsumeAsync(unsupported).AsTask(),
            "unsupported Make Attribute Stone schema is rejected");
    }
}
