using Godswar.Server.Application.Messaging;
using Godswar.Server.Infrastructure.Messaging;

namespace Godswar.Server.Infrastructure.FactionCrier;

internal sealed class FactionCrierOutboxConsumer : IOutboxEventConsumer
{
    public string ConsumerKey => FactionCrierPersistenceCodec.ConsumerKey;

    public OutboxOrderingPolicy OrderingPolicy =>
        OutboxOrderingPolicy.StrictSequence;

    public ValueTask ConsumeAsync(
        OutboxEventMessage message,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        cancellationToken.ThrowIfCancellationRequested();
        if (message.ConsumerKey != ConsumerKey ||
            message.AggregateType != FactionCrierPersistenceCodec.AggregateType ||
            message.EventType != FactionCrierPersistenceCodec.EventType ||
            message.SchemaVersion != FactionCrierPersistenceCodec.ContractVersion)
        {
            throw new InvalidDataException(
                "Faction Crier outbox routing metadata is invalid.");
        }

        var receipt = FactionCrierPersistenceCodec.Decode(
            message.Payload.Span);
        if (receipt.EventId != message.EventId ||
            receipt.FactionCrierRevision != message.AggregateRevision ||
            !string.Equals(
                FactionCrierPersistenceCodec.AggregateKey(receipt.CharacterId),
                message.AggregateKey,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Faction Crier outbox identity is invalid.");
        }
        return ValueTask.CompletedTask;
    }
}
