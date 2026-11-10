using Godswar.Server.Application.Messaging;
using Godswar.Server.Infrastructure.Messaging;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresOutboxHistoricalRepairIntegrationChecks
{
    public const string CheckName =
        "PostgreSQL historical outbox ordered-sparse repair";
    private const string ConnectionStringVariable =
        "GODSWAR_OUTBOX_REPAIR_TEST_CONNECTION_STRING";
    private const string PhaseVariable =
        "GODSWAR_OUTBOX_REPAIR_TEST_PHASE";
    private static readonly Guid PoisonEventId =
        Guid.Parse("b6753826-ebcd-4c40-91e3-e856b27621cb");

    public static async Task RunAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable(
            ConnectionStringVariable);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new CheckSkippedException($"{CheckName} ({ConnectionStringVariable} is not set)");
        }

        var phase = Environment.GetEnvironmentVariable(PhaseVariable)?
            .Trim().ToLowerInvariant() ?? "verify";
        Check.True(
            phase is "prepare" or "verify" or "diagnose",
            $"{PhaseVariable} is prepare, verify, or diagnose");

        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        if (phase == "diagnose")
        {
            await PredecodeAllPendingPetsAsync(dataSource);
            await PredecodeAllSparseTargetsAsync(dataSource);
            return;
        }
        if (phase == "prepare")
        {
            await PrepareDisposableBackupAsync(dataSource);
            return;
        }

        await new PostgresSchemaMigrationRunner(dataSource)
            .InitializeGodswarSchemaAsync();
        await AssertReplayReadyAsync(dataSource);

        var expected = await ReadPendingAsync(dataSource);
        Check.True(
            expected.Count == 2_215 &&
            expected.Count(row =>
                row.ConsumerKey == "inventory_projection_v1" &&
                row.AggregateKey == "character:2:inventory") == 705 &&
            expected.Count(row =>
                row.ConsumerKey == "inventory_projection_v1" &&
                row.AggregateKey == "character:7005:inventory") == 65 &&
            expected.Count(row =>
                row.ConsumerKey ==
                    "progression_reward_projection_v1") == 19 &&
            expected.Count(row =>
                row.ConsumerKey == "pet_durable_v1") == 1_426 &&
            expected.Count(row => row.EventId == PoisonEventId) == 1,
            "the reset clone has 2,214 historical rows plus poison v317");

        var tracking = PostgresOutboxConsumerCatalog.Create()
            .Select(consumer =>
                (IOutboxEventConsumer)new TrackingConsumer(consumer))
            .ToArray();
        var dispatcher = new PostgresOutboxDispatcher(
            dataSource,
            tracking,
            new PostgresOutboxDispatcherOptions
            {
                Enabled = true,
                BatchSize = 256,
                PollIntervalMilliseconds = 50,
                LeaseMilliseconds = 60_000,
                MaximumDeliveryAttempts = 8,
                BaseRetryDelayMilliseconds = 50,
                MaximumRetryDelayMilliseconds = 50,
                GapRetryDelayMilliseconds = 50,
                CommandTimeoutMilliseconds = 30_000
            },
            "historical-outbox-repair-check");

        var processed = 0;
        var remaining = await CountPendingAsync(dataSource);
        var drainDeadline = DateTimeOffset.UtcNow.AddMinutes(15);
        var passes = 0;
        while (remaining > 0 &&
               DateTimeOffset.UtcNow < drainDeadline &&
               passes < 10_000)
        {
            var count = await dispatcher.DispatchOnceAsync();
            processed += count;
            passes++;
            remaining = await CountPendingAsync(dataSource);
            if (remaining == 0)
            {
                break;
            }
            if (count == 0)
            {
                await Task.Delay(50);
            }
        }
        Check.True(
            remaining == 0,
            $"the bounded drain reaches every target stream tail; " +
            $"remaining={remaining}, passes={passes}");

        var recorded = tracking
            .Cast<TrackingConsumer>()
            .SelectMany(consumer => consumer.Messages)
            .Where(IsRepairTarget)
            .ToArray();
        var expectedIds = expected.Select(row => row.EventId).ToHashSet();
        var actualIds = recorded.Select(row => row.EventId).ToHashSet();
        Check.True(
            processed >= 2_215,
            $"dispatcher completed at least 2,215 rows; actual={processed}");
        Check.True(
            recorded.Length == 2_215,
            $"real target-consumer callback count is 2,215; " +
            $"actual={recorded.Length}");
        Check.True(
            actualIds.Count == 2_215,
            "every target consumer callback has a unique event ID");
        Check.True(
            expectedIds.SetEquals(actualIds),
            "the callback event-ID set equals the exact pre-drain set");
        Check.True(
            recorded.Any(message =>
                message.EventId == PoisonEventId &&
                message.SchemaVersion == 2 &&
                message.AggregateRevision == 317),
            "the exact poisoned V2 payload decodes through the current consumer");

        foreach (var stream in recorded.GroupBy(message =>
                     (message.ConsumerKey, message.AggregateKey)))
        {
            var revisions = stream
                .Select(message => message.AggregateRevision)
                .ToArray();
            Check.True(
                revisions.Zip(revisions.Skip(1),
                    static (left, right) => right > left).All(value => value),
                $"{stream.Key} was validated in increasing revision order");
        }

        await AssertFinalStateAsync(dataSource);
    }

    private static bool IsRepairTarget(OutboxEventMessage message) =>
        (message.ConsumerKey == "inventory_projection_v1" &&
         message.AggregateKey is
             "character:2:inventory" or
             "character:7005:inventory") ||
        (message.ConsumerKey == "progression_reward_projection_v1" &&
         message.AggregateKey == "character:2:progression") ||
        (message.ConsumerKey == "pet_durable_v1" &&
         message.AggregateKey == "character:2");

    private static async Task<List<PendingRow>> ReadPendingAsync(
        NpgsqlDataSource dataSource)
    {
        const string sql =
            """
            SELECT event_id, consumer_key, aggregate_key, aggregate_version
            FROM public.outbox_events
            WHERE delivered_at IS NULL
              AND poisoned_at IS NULL
              AND (
                  (consumer_key = 'inventory_projection_v1'
                   AND aggregate_key IN (
                       'character:2:inventory',
                       'character:7005:inventory'))
                  OR (consumer_key = 'progression_reward_projection_v1'
                      AND aggregate_key = 'character:2:progression')
                  OR (consumer_key = 'pet_durable_v1'
                      AND aggregate_key = 'character:2')
              )
            ORDER BY consumer_key, aggregate_key, aggregate_version;
            """;
        var rows = new List<PendingRow>();
        await using var command = dataSource.CreateCommand(sql);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(new PendingRow(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt64(3)));
        }
        return rows;
    }

    private static async Task<long> CountPendingAsync(
        NpgsqlDataSource dataSource)
    {
        const string sql =
            """
            SELECT count(*)
            FROM public.outbox_events
            WHERE delivered_at IS NULL
              AND poisoned_at IS NULL
              AND (
                  (consumer_key = 'inventory_projection_v1'
                   AND aggregate_key IN (
                       'character:2:inventory',
                       'character:7005:inventory'))
                  OR (consumer_key = 'progression_reward_projection_v1'
                      AND aggregate_key = 'character:2:progression')
                  OR (consumer_key = 'pet_durable_v1'
                      AND aggregate_key = 'character:2')
              );
            """;
        await using var command = dataSource.CreateCommand(sql);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static async Task AssertFinalStateAsync(
        NpgsqlDataSource dataSource)
    {
        const string sql =
            """
            SELECT
              (SELECT count(*) FROM public.outbox_events
               WHERE delivered_at IS NULL
                 AND ((consumer_key='inventory_projection_v1'
                       AND aggregate_key IN ('character:2:inventory',
                         'character:7005:inventory'))
                   OR (consumer_key='progression_reward_projection_v1'
                       AND aggregate_key='character:2:progression')
                   OR (consumer_key='pet_durable_v1'
                       AND aggregate_key='character:2'))),
              (SELECT count(*) FROM public.outbox_events
               WHERE poisoned_at IS NOT NULL
                 AND ((consumer_key='inventory_projection_v1'
                       AND aggregate_key IN ('character:2:inventory',
                         'character:7005:inventory'))
                   OR (consumer_key='progression_reward_projection_v1'
                       AND aggregate_key='character:2:progression')
                   OR (consumer_key='pet_durable_v1'
                       AND aggregate_key='character:2'))),
              (SELECT array_agg(current_version ORDER BY consumer_key,
                         aggregate_key)
               FROM public.outbox_consumer_positions
               WHERE (consumer_key,aggregate_key) IN (
                 ('inventory_projection_v1','character:2:inventory'),
                 ('inventory_projection_v1','character:7005:inventory'),
                 ('pet_durable_v1','character:2'),
                 ('progression_reward_projection_v1',
                    'character:2:progression'))),
              (SELECT count(*) FROM public.outbox_events
               WHERE consumer_key IN ('inventory_projection_v1',
                 'progression_reward_projection_v1')
                 AND ordering_policy<>'ordered_sparse'),
              (SELECT count(*) FROM public.outbox_consumer_positions
               WHERE consumer_key IN ('inventory_projection_v1',
                 'progression_reward_projection_v1')
                 AND ordering_policy<>'ordered_sparse'),
              (SELECT count(*) FROM public.command_audit
               WHERE command_family='outbox_poison_replay_repair'
                 AND operation_id=sha256(convert_to(
                   'localdev|outbox-poison-replay|pet_durable_v1|' ||
                   'character:2|v317|v1','UTF8'))
                 AND detail_payload->>'poisonReason'=
                   'consumer_failure_max_attempts'
                 AND detail_payload->>'attemptCountBefore'='8'
                 AND detail_payload->>'payloadSha256'=
                   'a0f41b055090797377f86874e7b1c57d0ed943a0e565ea2b6c6876136b6aafb6'),
              (SELECT attempt_count FROM public.outbox_events
               WHERE event_id=@event_id);
            """;
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("event_id", PoisonEventId);
        await using var reader = await command.ExecuteReaderAsync();
        Check.True(await reader.ReadAsync(), "repair final state exists");
        var positions = (long[])reader.GetValue(2);
        Check.True(
            reader.GetInt64(0) == 0 &&
            reader.GetInt64(1) == 0 &&
            positions.SequenceEqual([738L, 75L, 1_742L, 32L]) &&
            reader.GetInt64(3) == 0 &&
            reader.GetInt64(4) == 0 &&
            reader.GetInt64(5) == 1 &&
            reader.GetInt16(6) == 1,
            "all four positions advance and poison history stays audited");
    }

    private sealed class TrackingConsumer(IOutboxEventConsumer inner) :
        IOutboxEventConsumer
    {
        private readonly List<OutboxEventMessage> _messages = [];

        public string ConsumerKey => inner.ConsumerKey;

        public OutboxOrderingPolicy OrderingPolicy => inner.OrderingPolicy;

        public IReadOnlyList<OutboxEventMessage> Messages => _messages;

        public async ValueTask ConsumeAsync(
            OutboxEventMessage message,
            CancellationToken cancellationToken = default)
        {
            await inner.ConsumeAsync(message, cancellationToken);
            _messages.Add(message);
        }
    }

    private readonly record struct PendingRow(
        Guid EventId,
        string ConsumerKey,
        string AggregateKey,
        long AggregateRevision);
}
