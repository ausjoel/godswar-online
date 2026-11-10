using Godswar.Server.Application.Messaging;

namespace Godswar.Server.Infrastructure.Inventory;

internal sealed partial class CharacterInventoryOutboxConsumer
{
    private static bool TryValidateHolySuit(OutboxEventMessage message)
    {
        if (!HolySuitPersistenceCodec.IsEventType(message.EventType) ||
            message.SchemaVersion !=
                HolySuitPersistenceCodec.ContractVersion)
        {
            return false;
        }

        var receipt = HolySuitPersistenceCodec.Decode(
            message.Payload.Span);
        if (!receipt.Committed ||
            !string.Equals(
                HolySuitPersistenceCodec.EventType(receipt.Family),
                message.EventType,
                StringComparison.Ordinal) ||
            receipt.OutboxEventId != message.EventId ||
            receipt.InventoryRevision != message.AggregateRevision ||
            !string.Equals(
                HolySuitPersistenceCodec.AggregateKey(
                    receipt.CharacterId),
                message.AggregateKey,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The Holy Suit outbox identity is inconsistent.");
        }

        return true;
    }
}
