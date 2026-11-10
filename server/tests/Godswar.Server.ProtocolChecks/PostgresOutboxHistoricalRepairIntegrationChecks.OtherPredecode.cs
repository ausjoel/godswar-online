using System.Text;
using Godswar.Server.Application.Messaging;
using Godswar.Server.Infrastructure.Inventory;
using Godswar.Server.Infrastructure.Rewards;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresOutboxHistoricalRepairIntegrationChecks
{
    private static async Task PredecodeAllSparseTargetsAsync(
        NpgsqlDataSource dataSource)
    {
        const string sql =
            """
            SELECT
                event.id,
                event.event_id,
                event.command_inbox_id,
                event.consumer_key,
                event.aggregate_type,
                event.aggregate_key,
                event.aggregate_version,
                event.event_type,
                event.contract_version,
                event.created_at,
                event.payload::text,
                encode(sha256(convert_to(
                    event.payload::text, 'UTF8')), 'hex'),
                COALESCE(encode(inbox.result_hash, 'hex'), 'NULL'),
                event.attempt_count,
                event.max_attempts,
                event.poisoned_at,
                event.poison_reason,
                inbox.result_payload::text,
                inbox.result_hash,
                inbox.result_code,
                inbox.audit_id
            FROM public.outbox_events event
            JOIN public.command_inbox inbox
              ON inbox.id = event.command_inbox_id
            WHERE event.delivered_at IS NULL
              AND (
                (event.consumer_key = 'inventory_projection_v1'
                 AND event.aggregate_key IN (
                   'character:2:inventory',
                   'character:7005:inventory'))
                OR
                (event.consumer_key = 'progression_reward_projection_v1'
                 AND event.aggregate_key = 'character:2:progression'))
            ORDER BY event.consumer_key, event.aggregate_key,
                event.aggregate_version;
            """;
        var rows = new List<SparseTargetDiagnosticRow>(789);
        await using (var command = dataSource.CreateCommand(sql))
        await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                rows.Add(new SparseTargetDiagnosticRow(
                    reader.GetInt64(0),
                    reader.GetInt64(2),
                    reader.GetString(11),
                    reader.GetString(12),
                    reader.GetInt16(13),
                    reader.GetInt16(14),
                    reader.IsDBNull(15)
                        ? null
                        : new DateTimeOffset(reader.GetDateTime(15)),
                    reader.IsDBNull(16) ? null : reader.GetString(16),
                    reader.IsDBNull(17) ? null : reader.GetString(17),
                    reader.IsDBNull(18)
                        ? null
                        : (byte[])reader.GetValue(18),
                    reader.IsDBNull(19) ? null : reader.GetString(19),
                    reader.IsDBNull(20) ? null : reader.GetInt64(20),
                    new OutboxEventMessage(
                        reader.GetGuid(1),
                        reader.GetString(3),
                        reader.GetString(4),
                        reader.GetString(5),
                        reader.GetInt64(6),
                        reader.GetString(7),
                        reader.GetInt16(8),
                        new DateTimeOffset(reader.GetDateTime(9)),
                        Encoding.UTF8.GetBytes(reader.GetString(10)))));
            }
        }

        Check.True(
            rows.Count == 789 &&
            rows.Count(row => row.Message.ConsumerKey ==
                "inventory_projection_v1" &&
                row.Message.AggregateKey ==
                    "character:2:inventory") == 705 &&
            rows.Count(row => row.Message.ConsumerKey ==
                "inventory_projection_v1" &&
                row.Message.AggregateKey ==
                    "character:7005:inventory") == 65 &&
            rows.Count(row => row.Message.ConsumerKey ==
                "progression_reward_projection_v1") == 19 &&
            rows.Select(row => row.Message.EventId).Distinct().Count() ==
                789,
            "all 789 sparse target events are present exactly once");

        IOutboxEventConsumer inventory =
            new CharacterInventoryOutboxConsumer();
        IOutboxEventConsumer progression =
            new MonsterDeathRewardOutboxConsumer();
        var failures = new List<string>();
        var holySuitCount = 0;
        foreach (var row in rows)
        {
            if (HolySuitPersistenceCodec.IsEventType(
                    row.Message.EventType))
            {
                holySuitCount++;
                try
                {
                    var receipt = HolySuitPersistenceCodec.Decode(
                        row.Message.Payload.Span);
                    if (row.InboxPayload is null ||
                        row.InboxResultHash is null ||
                        row.InboxResultCode is null ||
                        !row.InboxAuditId.HasValue)
                    {
                        throw new InvalidDataException(
                            "The Holy Suit inbox evidence is incomplete.");
                    }
                    _ = HolySuitPersistenceCodec.DecodeAndVerify(
                        row.InboxPayload,
                        row.InboxResultHash,
                        row.InboxResultCode,
                        row.InboxAuditId.Value,
                        receipt.Family);
                }
                catch (Exception exception)
                {
                    failures.Add(DescribeSparseFailure(
                        row,
                        "holy_suit_codec_or_hash",
                        exception));
                }
            }

            try
            {
                var consumer = row.Message.ConsumerKey ==
                    inventory.ConsumerKey
                        ? inventory
                        : progression;
                await consumer.ConsumeAsync(row.Message);
            }
            catch (Exception exception)
            {
                failures.Add(DescribeSparseFailure(
                    row,
                    "consumer",
                    exception));
            }
        }
        Check.True(
            holySuitCount == 39,
            "all 39 historical Holy Suit rows were codec/hash verified");
        if (failures.Count > 0)
        {
            throw new InvalidDataException(
                $"Sparse-target predecode found {failures.Count} " +
                "failure(s):" + Environment.NewLine +
                string.Join(Environment.NewLine, failures));
        }

        Console.WriteLine(
            "Predecoded all 770 inventory and 19 progression target rows, " +
            "including 39 inbox-hash-verified Holy Suit rows.");
    }

    private static string DescribeSparseFailure(
        SparseTargetDiagnosticRow row,
        string stage,
        Exception exception) =>
        $"row={row.RowId}|event={row.Message.EventId}|" +
        $"inbox={row.InboxId}|consumer={row.Message.ConsumerKey}|" +
        $"key={row.Message.AggregateKey}|version=" +
        $"{row.Message.AggregateRevision}|type={row.Message.EventType}|" +
        $"contract={row.Message.SchemaVersion}|payloadSha=" +
        $"{row.PayloadSha256}|resultHash={row.ResultHash}|" +
        $"attempts={row.AttemptCount}/{row.MaxAttempts}|" +
        $"poisonedAt={row.PoisonedAt?.ToString("O") ?? "NULL"}|" +
        $"poisonReason={row.PoisonReason ?? "NULL"}|stage={stage}|" +
        $"exception={exception.GetType().FullName}: {exception.Message}";

    private readonly record struct SparseTargetDiagnosticRow(
        long RowId,
        long InboxId,
        string PayloadSha256,
        string ResultHash,
        short AttemptCount,
        short MaxAttempts,
        DateTimeOffset? PoisonedAt,
        string? PoisonReason,
        string? InboxPayload,
        byte[]? InboxResultHash,
        string? InboxResultCode,
        long? InboxAuditId,
        OutboxEventMessage Message);
}
