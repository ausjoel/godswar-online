using Godswar.Server.Application.Messaging;
using Godswar.Server.Infrastructure.Pets;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresOutboxHistoricalRepairIntegrationChecks
{
    private static async Task PrepareDisposableBackupAsync(
        NpgsqlDataSource dataSource)
    {
        var baseline = await ReadPendingAsync(dataSource);
        Check.True(
            baseline.Count == 2_214 &&
            baseline.Count(row =>
                row.ConsumerKey == "inventory_projection_v1" &&
                row.AggregateKey == "character:2:inventory") == 705 &&
            baseline.Count(row =>
                row.ConsumerKey == "inventory_projection_v1" &&
                row.AggregateKey == "character:7005:inventory") == 65 &&
            baseline.Count(row =>
                row.ConsumerKey ==
                    "progression_reward_projection_v1") == 19 &&
            baseline.Count(row =>
                row.ConsumerKey == "pet_durable_v1") == 1_425 &&
            baseline.All(row => row.EventId != PoisonEventId),
            "the logical backup starts with exactly 2,214 ready rows");

        var positionsBefore = await ReadPositionVersionsAsync(dataSource);
        Check.True(
            positionsBefore.SequenceEqual([0L, 0L, 316L, 0L]),
            "the blocked backup checkpoints are exactly 0, 0, 316, and 0");

        var poison = await ReadExactPoisonMessageAsync(dataSource);
        await new PetDurableOutboxConsumer().ConsumeAsync(poison);
        await PredecodeAllPendingPetsAsync(dataSource);
        await PredecodeAllSparseTargetsAsync(dataSource);
        Console.WriteLine(
            "Prevalidated the complete 2,215-row repair callback set.");

        await new PostgresSchemaMigrationRunner(dataSource)
            .InitializeGodswarSchemaAsync();

        var afterMigration = await ReadPendingAsync(dataSource);
        Check.True(
            baseline.Select(row => row.EventId).SequenceEqual(
                afterMigration.Select(row => row.EventId)) &&
            baseline.Select(row => row.AggregateRevision).SequenceEqual(
                afterMigration.Select(row => row.AggregateRevision)),
            "migration 122 neither delivers nor synthesizes historical rows");
        var positionsAfter = await ReadPositionVersionsAsync(dataSource);
        Check.True(
            positionsBefore.SequenceEqual(positionsAfter),
            "migration 122 does not rebaseline any checkpoint");

        await ReadExactPoisonMessageAsync(dataSource);
        await AssertPreparedStateAsync(dataSource);
        Console.WriteLine(
            "Prepared 2,214 ready rows plus exact poisoned V2 v317; " +
            "migration 122 preserved every checkpoint.");
    }

    private static async Task<OutboxEventMessage>
        ReadExactPoisonMessageAsync(NpgsqlDataSource dataSource)
    {
        const string sql =
            """
            SELECT
                event.event_id,
                event.consumer_key,
                event.aggregate_type,
                event.aggregate_key,
                event.aggregate_version,
                event.event_type,
                event.contract_version,
                event.created_at,
                event.payload::text,
                event.id = 6774
                  AND event.command_inbox_id = 6719
                  AND event.consumer_key = 'pet_durable_v1'
                  AND event.aggregate_type = 'character_pet_value'
                  AND event.aggregate_key = 'character:2'
                  AND event.aggregate_version = 317
                  AND event.event_type = 'pet.basic_savvy_reset'
                  AND event.contract_version = 2
                  AND event.ordering_policy = 'strict'
                  AND event.attempt_count = 8
                  AND event.max_attempts = 8
                  AND event.delivered_at IS NULL
                  AND event.poisoned_at =
                      '2026-08-11 14:43:23.374761+00'::timestamptz
                  AND event.poison_reason =
                      'consumer_failure_max_attempts'
                  AND event.created_at =
                      '2026-08-11 14:41:57.319068+00'::timestamptz
                  AND event.lease_token IS NULL
                  AND encode(sha256(convert_to(
                      event.payload::text, 'UTF8')), 'hex') =
                      'a0f41b055090797377f86874e7b1c57d0ed943a0e565ea2b6c6876136b6aafb6'
            FROM public.outbox_events event
            WHERE event.event_id = @event_id;
            """;
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("event_id", PoisonEventId);
        await using var reader = await command.ExecuteReaderAsync();
        Check.True(await reader.ReadAsync(), "the exact poison row exists");
        Check.True(reader.GetBoolean(9), "the poison authority is unchanged");
        var message = new OutboxEventMessage(
            reader.GetGuid(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetInt64(4),
            reader.GetString(5),
            reader.GetInt16(6),
            new DateTimeOffset(reader.GetDateTime(7)),
            System.Text.Encoding.UTF8.GetBytes(reader.GetString(8)));
        Check.True(!await reader.ReadAsync(), "the poison event ID is unique");
        return message;
    }

    private static async Task<long[]> ReadPositionVersionsAsync(
        NpgsqlDataSource dataSource)
    {
        const string sql =
            """
            SELECT current_version
            FROM public.outbox_consumer_positions
            WHERE (consumer_key, aggregate_key) IN (
                ('inventory_projection_v1','character:2:inventory'),
                ('inventory_projection_v1','character:7005:inventory'),
                ('pet_durable_v1','character:2'),
                ('progression_reward_projection_v1',
                    'character:2:progression'))
            ORDER BY consumer_key, aggregate_key;
            """;
        var versions = new List<long>();
        await using var command = dataSource.CreateCommand(sql);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            versions.Add(reader.GetInt64(0));
        }
        Check.True(versions.Count == 4, "all four blocked positions exist");
        return versions.ToArray();
    }

    private static async Task AssertPreparedStateAsync(
        NpgsqlDataSource dataSource)
    {
        const string sql =
            """
            SELECT
              (SELECT count(*) FROM public.schema_migrations
               WHERE migration_id=
                 '20260830_122_outbox_ordered_sparse'),
              (SELECT count(*) FROM public.outbox_events
               WHERE consumer_key IN ('inventory_projection_v1',
                   'progression_reward_projection_v1')
                 AND ordering_policy <> 'ordered_sparse'),
              (SELECT count(*) FROM public.outbox_consumer_positions
               WHERE consumer_key IN ('inventory_projection_v1',
                   'progression_reward_projection_v1')
                 AND ordering_policy <> 'ordered_sparse'),
              (SELECT count(*) FROM public.command_audit
               WHERE command_family='outbox_poison_replay_repair'),
              (SELECT count(*) FROM pg_trigger
               WHERE tgrelid IN ('public.outbox_events'::regclass,
                   'public.outbox_consumer_positions'::regclass)
                 AND tgname IN ('trg_outbox_events_guard',
                   'trg_outbox_events_lease_consistency',
                   'trg_outbox_consumer_positions_guard',
                   'trg_outbox_positions_lease_consistency')
                 AND tgenabled='O'),
              (SELECT count(*) FROM pg_trigger
               WHERE tgrelid='public.command_audit'::regclass
                 AND tgname='trg_command_audit_immutable'
                 AND tgenabled='O'),
              (SELECT count(*) FROM pg_constraint
               WHERE conrelid IN ('public.outbox_events'::regclass,
                   'public.outbox_consumer_positions'::regclass)
                 AND conname IN (
                   'ck_outbox_events_sparse_consumer_policy',
                   'ck_outbox_positions_sparse_consumer_policy')),
              (SELECT count(*) FROM public.schema_migrations
               WHERE migration_id=
                   '20260830_123_outbox_claim_candidate_index'
                 AND checksum=
                   '232F53D0C36BEDF4DCC729F486ADE852FFE7E17880F21FAA91D16161D6524030'),
              ((to_regclass(
                   'public.ix_outbox_events_claimable_stream')
                   IS NOT NULL)::int
               + (to_regclass(
                   'public.ix_outbox_events_ordered_stream_version')
                   IS NOT NULL)::int);
            """;
        await using var command = dataSource.CreateCommand(sql);
        await using var reader = await command.ExecuteReaderAsync();
        Check.True(await reader.ReadAsync(), "prepared state exists");
        Check.True(
            reader.GetInt64(0) == 1 &&
            reader.GetInt64(1) == 0 &&
            reader.GetInt64(2) == 0 &&
            reader.GetInt64(3) == 0 &&
            reader.GetInt64(4) == 4 &&
            reader.GetInt64(5) == 1 &&
            reader.GetInt64(6) == 2 &&
            reader.GetInt64(7) == 1 &&
            reader.GetInt32(8) == 2,
            "migration 122 converts only sparse policies and keeps guards");
    }

    private static async Task AssertReplayReadyAsync(
        NpgsqlDataSource dataSource)
    {
        const string sql =
            """
            SELECT
              EXISTS (SELECT 1 FROM public.outbox_events event
               WHERE event.id=6774
                 AND event.event_id=@event_id
                 AND event.command_inbox_id=6719
                 AND event.consumer_key='pet_durable_v1'
                 AND event.aggregate_type='character_pet_value'
                 AND event.aggregate_key='character:2'
                 AND event.aggregate_version=317
                 AND event.event_type='pet.basic_savvy_reset'
                 AND event.contract_version=2
                 AND event.ordering_policy='strict'
                 AND event.attempt_count=0
                 AND event.max_attempts=8
                 AND event.delivered_at IS NULL
                 AND event.poisoned_at IS NULL
                 AND event.poison_reason IS NULL
                 AND event.lease_token IS NULL
                 AND encode(sha256(convert_to(
                     event.payload::text,'UTF8')),'hex')=
                   'a0f41b055090797377f86874e7b1c57d0ed943a0e565ea2b6c6876136b6aafb6'),
              EXISTS (SELECT 1 FROM public.outbox_consumer_positions
               WHERE consumer_key='pet_durable_v1'
                 AND aggregate_type='character_pet_value'
                 AND aggregate_key='character:2'
                 AND ordering_policy='strict'
                 AND current_version=316
                 AND inflight_event_id IS NULL),
              (SELECT count(*) FROM public.command_audit
               WHERE command_family='outbox_poison_replay_repair'
                 AND operation_id=sha256(convert_to(
                   'localdev|outbox-poison-replay|pet_durable_v1|' ||
                   'character:2|v317|v1','UTF8'))
                 AND outcome_code='repaired'
                 AND retention_policy='permanent'
                 AND detail_payload->>'poisonReason'=
                   'consumer_failure_max_attempts'
                 AND detail_payload->>'attemptCountBefore'='8'
                 AND detail_payload->>'attemptCountAfter'='0'
                 AND detail_payload->>'payloadSha256'=
                   'a0f41b055090797377f86874e7b1c57d0ed943a0e565ea2b6c6876136b6aafb6'),
              (SELECT count(*) FROM public.outbox_events
               WHERE consumer_key IN ('inventory_projection_v1',
                   'progression_reward_projection_v1')
                 AND ordering_policy<>'ordered_sparse'),
              (SELECT count(*) FROM public.outbox_consumer_positions
               WHERE consumer_key IN ('inventory_projection_v1',
                   'progression_reward_projection_v1')
                 AND ordering_policy<>'ordered_sparse');
            """;
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("event_id", PoisonEventId);
        await using var reader = await command.ExecuteReaderAsync();
        Check.True(await reader.ReadAsync(), "replay-ready state exists");
        Check.True(
            reader.GetBoolean(0) &&
            reader.GetBoolean(1) &&
            reader.GetInt64(2) == 1 &&
            reader.GetInt64(3) == 0 &&
            reader.GetInt64(4) == 0,
            "the audited exact poison reset is ready behind v316");
    }
}
