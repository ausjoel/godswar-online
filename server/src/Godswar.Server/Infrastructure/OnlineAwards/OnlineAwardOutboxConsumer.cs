using Godswar.Server.Application.Messaging;
using Godswar.Server.Infrastructure.Messaging;

namespace Godswar.Server.Infrastructure.OnlineAwards;

internal sealed class OnlineAwardOutboxConsumer : IOutboxEventConsumer
{
    public string ConsumerKey => OnlineAwardPersistenceCodec.ConsumerKey;

    public OutboxOrderingPolicy OrderingPolicy =>
        OutboxOrderingPolicy.StrictSequence;

    public ValueTask ConsumeAsync(
        OutboxEventMessage message,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        cancellationToken.ThrowIfCancellationRequested();
        if (message.ConsumerKey != ConsumerKey ||
            message.AggregateType != OnlineAwardPersistenceCodec.AggregateType ||
            message.EventType != OnlineAwardPersistenceCodec.EventType ||
            message.SchemaVersion !=
                OnlineAwardPersistenceCodec.ContractVersion)
        {
            throw new InvalidDataException(
                "Online Award outbox routing metadata is invalid.");
        }

        var receipt = OnlineAwardPersistenceCodec.Decode(message.Payload.Span);
        if (receipt.EventId != message.EventId ||
            receipt.OnlineAwardRevision != message.AggregateRevision ||
            OnlineAwardPersistenceCodec.AggregateKey(receipt.CharacterId) !=
                message.AggregateKey)
        {
            throw new InvalidDataException(
                "Online Award outbox identity is invalid.");
        }
        return ValueTask.CompletedTask;
    }
}
