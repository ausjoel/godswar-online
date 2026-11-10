using System.Text;
using Godswar.Server.Application.Messaging;
using Godswar.Server.Infrastructure.Pets;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresOutboxHistoricalRepairIntegrationChecks
{
    private static async Task PredecodeAllPendingPetsAsync(
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
                event.aggregate_version = 1725
                  AND event.id = 9694
                  AND event.event_id =
                    '736f4cb1-0434-481d-af70-6dc3e0dca11f'::uuid
                  AND event.command_inbox_id = 9208
                  AND event.event_type = 'pet.manager_utility'
                  AND event.contract_version = 1
                  AND event.ordering_policy = 'strict'
                  AND encode(sha256(convert_to(
                      event.payload::text, 'UTF8')), 'hex') =
                    '82d2041e0fae62d7647935cd82aef7c5e82abb640b96cb4d0fa9c07501db93bf'
                  AND inbox.result_payload = event.payload
                  AND encode(inbox.result_hash, 'hex') =
                    'e30466b58a4e6c612943f28ef7c5b86e95de465a1d98ec10bff1967a85582af9',
                event.aggregate_version = 1729
                  AND event.id = 9721
                  AND event.event_id =
                    '829a79e3-9232-4a33-8d7a-e87f6268d52b'::uuid
                  AND event.command_inbox_id = 9244
                  AND event.event_type = 'pet.manager_utility'
                  AND event.contract_version = 1
                  AND event.ordering_policy = 'strict'
                  AND encode(sha256(convert_to(
                      event.payload::text, 'UTF8')), 'hex') =
                    '28e54e52e4056eeb54cbaa8ce5e329a3a93d3cc9e9b04344b2309ed40ffa99c8'
                  AND inbox.result_payload = event.payload
                  AND encode(inbox.result_hash, 'hex') =
                    '9626feb17929dde0a9440eab960c95709d6b5d0bbb304bfdbcbf9fa591f9c5ee',
                inbox.result_payload::text,
                inbox.result_hash,
                encode(sha256(convert_to(
                    event.payload::text, 'UTF8')), 'hex'),
                COALESCE(encode(inbox.result_hash, 'hex'), 'NULL')
            FROM public.outbox_events event
            JOIN public.command_inbox inbox
              ON inbox.id = event.command_inbox_id
            WHERE event.consumer_key = 'pet_durable_v1'
              AND event.aggregate_type = 'character_pet_value'
              AND event.aggregate_key = 'character:2'
              AND event.delivered_at IS NULL
            ORDER BY event.aggregate_version;
            """;
        var rows = new List<PendingPetDiagnosticRow>(1_426);
        var exactLegacySealCount = 0;
        var exactLegacyUnsealCount = 0;
        await using (var command = dataSource.CreateCommand(sql))
        await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                var message = new OutboxEventMessage(
                    reader.GetGuid(1),
                    reader.GetString(3),
                    reader.GetString(4),
                    reader.GetString(5),
                    reader.GetInt64(6),
                    reader.GetString(7),
                    reader.GetInt16(8),
                    new DateTimeOffset(reader.GetDateTime(9)),
                    Encoding.UTF8.GetBytes(reader.GetString(10)));
                rows.Add(new PendingPetDiagnosticRow(
                    reader.GetInt64(0),
                    reader.GetInt64(2),
                    reader.GetString(15),
                    reader.GetString(16),
                    message));
                if (message.AggregateRevision == 1725)
                {
                    Check.True(
                        reader.GetBoolean(11),
                        "pet v1725 is the exact immutable legacy Seal row");
                    var inboxReceipt =
                        PetDurablePersistenceCodec.DecodeAndVerify(
                            reader.GetString(13),
                            (byte[])reader.GetValue(14));
                    Check.True(
                        !inboxReceipt.IsCarried &&
                        !inboxReceipt.IsSummoned &&
                        inboxReceipt.PetManagerUtility?.BeforePetState is
                            { IsCarried: true, IsSummoned: true } &&
                        inboxReceipt.PetManagerUtility?.AfterPetState is
                            { IsCarried: false, IsSummoned: false },
                        "pet v1725 verifies its hash and normalizes to After");
                    exactLegacySealCount++;
                    continue;
                }
                if (message.AggregateRevision == 1729)
                {
                    Check.True(
                        reader.GetBoolean(12),
                        "pet v1729 is the exact immutable legacy Unseal row");
                    var inboxReceipt =
                        PetDurablePersistenceCodec.DecodeAndVerify(
                            reader.GetString(13),
                            (byte[])reader.GetValue(14));
                    Check.True(
                        inboxReceipt.IsCarried &&
                        inboxReceipt.IsSummoned &&
                        inboxReceipt.PetManagerUtility?
                            .AuthenticatedLegacyActiveUnseal == true &&
                        inboxReceipt.PetManagerUtility?.BeforePetState is
                            { IsCarried: false, IsSummoned: false } &&
                        inboxReceipt.PetManagerUtility?.AfterPetState is
                            { IsCarried: true, IsSummoned: true },
                        "pet v1729 verifies its hash without inventing energy");
                    exactLegacyUnsealCount++;
                }
            }
        }

        Check.True(
            rows.Count == 1_426 &&
            exactLegacySealCount == 1 &&
            exactLegacyUnsealCount == 1 &&
            rows.Select(row => row.Message.AggregateRevision)
                .SequenceEqual(
                    Enumerable.Range(317, 1_426)
                        .Select(static version => (long)version)) &&
            rows.Select(row => row.Message.EventId).Distinct().Count() ==
                1_426 &&
            rows.Count(row => row.Message.EventId == PoisonEventId) == 1,
            "all 1,426 pending pet rows form the exact v317-v1742 stream");

        var consumer = new PetDurableOutboxConsumer();
        var failures = new List<string>();
        foreach (var row in rows)
        {
            try
            {
                await consumer.ConsumeAsync(row.Message);
            }
            catch (Exception exception)
            {
                failures.Add(
                    $"row={row.RowId}|event={row.Message.EventId}|" +
                    $"inbox={row.InboxId}|version=" +
                    $"{row.Message.AggregateRevision}|type=" +
                    $"{row.Message.EventType}|contract=" +
                    $"{row.Message.SchemaVersion}|payloadSha=" +
                    $"{row.PayloadSha256}|resultHash={row.ResultHash}|" +
                    $"exception={exception.GetType().FullName}: " +
                    exception.Message);
            }
        }
        if (failures.Count > 0)
        {
            throw new InvalidDataException(
                $"Pending pet predecode found {failures.Count} failure(s):" +
                Environment.NewLine + string.Join(
                    Environment.NewLine,
                    failures));
        }
        Console.WriteLine(
            "Predecoded all 1,426 pending pet rows, including exact v317 " +
            "and hash-verified legacy v1725 Seal and v1729 Unseal.");
    }

    private readonly record struct PendingPetDiagnosticRow(
        long RowId,
        long InboxId,
        string PayloadSha256,
        string ResultHash,
        OutboxEventMessage Message);
}
