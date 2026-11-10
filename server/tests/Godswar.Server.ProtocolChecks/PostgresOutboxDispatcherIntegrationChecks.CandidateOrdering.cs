using Godswar.Server.Application.Messaging;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresOutboxDispatcherIntegrationChecks
{
    private static async Task CheckStrictHeadSchedulingAsync(
        NpgsqlDataSource dataSource,
        CommandFixture fixture)
    {
        const string consumerKey = "checks.outbox.strict_head";
        var aggregateKey = NewAggregateKey("strict-head");
        var first = await InsertEventAsync(
            dataSource,
            fixture,
            consumerKey,
            aggregateKey,
            revision: 1,
            orderingPolicy: "strict");
        var second = await InsertEventAsync(
            dataSource,
            fixture,
            consumerKey,
            aggregateKey,
            revision: 2,
            orderingPolicy: "strict");
        await DelayAvailabilityAsync(
            dataSource,
            first.RowId,
            TimeSpan.FromMinutes(1));

        var consumer = new RecordingConsumer(
            consumerKey,
            OutboxOrderingPolicy.StrictSequence);
        var dispatcher = CreateDispatcher(
            dataSource,
            consumer,
            "checks-strict-head",
            batchSize: 1);

        Check.Equal(
            0,
            await dispatcher.DispatchOnceAsync(),
            "strict ordering waits for its lower scheduled revision");
        Check.Equal(
            0,
            (await ReadEventAsync(dataSource, second.RowId)).AttemptCount,
            "a due higher strict revision is not churned as a gap");

        await MakeAvailableAsync(dataSource, first.RowId);
        Check.Equal(
            1,
            await dispatcher.DispatchOnceAsync(),
            "strict ordering delivers its lower revision when due");
        Check.Equal(
            1,
            await dispatcher.DispatchOnceAsync(),
            "strict ordering then advances to the higher revision");
        Check.True(
            consumer.Revisions.SequenceEqual([1L, 2L]),
            "strict scheduling remains revision ordered when retry times invert");
    }

    private static async Task CheckOrderedSparseHeadSchedulingAsync(
        NpgsqlDataSource dataSource,
        CommandFixture fixture)
    {
        const string consumerKey = "checks.outbox.sparse_head";
        var aggregateKey = NewAggregateKey("sparse-head");
        var lower = await InsertEventAsync(
            dataSource,
            fixture,
            consumerKey,
            aggregateKey,
            revision: 2,
            orderingPolicy: "ordered_sparse");
        var higher = await InsertEventAsync(
            dataSource,
            fixture,
            consumerKey,
            aggregateKey,
            revision: 8,
            orderingPolicy: "ordered_sparse");
        await DelayAvailabilityAsync(
            dataSource,
            lower.RowId,
            TimeSpan.FromMinutes(1));

        var consumer = new RecordingConsumer(
            consumerKey,
            OutboxOrderingPolicy.OrderedSparse);
        var dispatcher = CreateDispatcher(
            dataSource,
            consumer,
            "checks-sparse-head",
            batchSize: 1);

        Check.Equal(
            0,
            await dispatcher.DispatchOnceAsync(),
            "ordered-sparse delivery waits for its lowest existing revision");
        Check.Equal(
            0,
            (await ReadEventAsync(dataSource, higher.RowId)).AttemptCount,
            "a later sparse revision cannot bypass a delayed stream head");

        await MakeAvailableAsync(dataSource, lower.RowId);
        Check.Equal(
            1,
            await dispatcher.DispatchOnceAsync(),
            "ordered-sparse delivery accepts a non-contiguous lower revision");
        Check.Equal(
            1,
            await dispatcher.DispatchOnceAsync(),
            "ordered-sparse delivery advances to the next existing revision");
        Check.True(
            consumer.Revisions.SequenceEqual([2L, 8L]),
            "ordered-sparse callbacks retain increasing revision order");
    }

    private static async Task CheckOrderedSparsePoisonBlockingAsync(
        NpgsqlDataSource dataSource,
        CommandFixture fixture)
    {
        const string consumerKey = "checks.outbox.sparse_poison";
        var aggregateKey = NewAggregateKey("sparse-poison");
        var lower = await InsertEventAsync(
            dataSource,
            fixture,
            consumerKey,
            aggregateKey,
            revision: 3,
            orderingPolicy: "ordered_sparse",
            maximumAttempts: 1);
        var higher = await InsertEventAsync(
            dataSource,
            fixture,
            consumerKey,
            aggregateKey,
            revision: 9,
            orderingPolicy: "ordered_sparse",
            maximumAttempts: 1);
        var consumer = new AlwaysFailingConsumer(
            consumerKey,
            OutboxOrderingPolicy.OrderedSparse);
        var dispatcher = CreateDispatcher(
            dataSource,
            consumer,
            "checks-sparse-poison",
            batchSize: 1,
            maximumAttempts: 1);

        Check.Equal(
            1,
            await dispatcher.DispatchOnceAsync(),
            "the lowest sparse revision reaches its bounded attempt");
        Check.True(
            (await ReadEventAsync(dataSource, lower.RowId))
                .PoisonedAtUtc.HasValue,
            "the failed sparse head becomes poisoned");
        Check.Equal(
            0,
            await dispatcher.DispatchOnceAsync(),
            "a poisoned sparse head blocks later revisions");
        Check.Equal(
            0,
            (await ReadEventAsync(dataSource, higher.RowId)).AttemptCount,
            "the blocked sparse successor consumes no attempt");
        Check.Equal(
            1,
            consumer.AttemptCount,
            "the poisoned sparse stream does not call the successor consumer");
    }
}
