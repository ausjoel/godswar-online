using System.Globalization;
using Godswar.Server.Application.OnlineAwards;
using Godswar.Server.Application.Realms;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Infrastructure.OnlineAwards;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresOnlineAwardIntegrationChecks
{
    private static readonly DateTimeOffset ReviewedInstant =
        new(2026, 8, 21, 10, 0, 0, TimeSpan.Zero);

    private static async Task<HistoricalClaim>
        AssertCommitReplayAndDailyFenceAsync(
        NpgsqlDataSource dataSource,
        PostgresOnlineAwardCommandExecutor executor,
        OnlineAwardFixture fixture,
        RealmCalendar calendar,
        OnlineAwardBalanceSnapshot balance)
    {
        var operationId = Guid.NewGuid();
        var envelope = CreateEnvelope(
            fixture,
            calendar,
            operationId,
            ReviewedInstant);
        var committed = await executor.ExecuteAsync(envelope);
        Check.Equal(
            (int)OnlineAwardExecutionDisposition.Committed,
            (int)committed.Disposition,
            "Online Award claim commits");
        var receipt = committed.Receipt ?? throw new InvalidDataException(
            "Committed Online Award claim omitted its receipt.");
        Check.True(
            receipt.BalanceRevision == balance.Revision &&
            receipt.BalanceSha256 == balance.Sha256 &&
            receipt.ItemDeltas.SequenceEqual(BaselineRewards().Select(
                static reward => new OnlineAwardItemDelta(
                    reward.ItemId,
                    reward.ItemQuality,
                    reward.Bound,
                    reward.Quantity))) &&
            receipt.InventoryRevision == 1 &&
            receipt.OnlineAwardRevision == 1,
            "Online Award receipt pins exact balance, items, and revisions");

        var duplicate = await executor.ExecuteAsync(CreateEnvelope(
            fixture,
            calendar,
            operationId,
            ReviewedInstant,
            npcId: checked((int)OnlineAwardProtocol.SpartaNpcId)));
        Check.True(
            duplicate.Disposition ==
                OnlineAwardExecutionDisposition.Duplicate &&
            HasSameReceipt(duplicate.Receipt, receipt),
            "same Online Award operation canonically replays across cities");

        var already = await executor.ExecuteAsync(CreateEnvelope(
            fixture,
            calendar,
            Guid.NewGuid(),
            ReviewedInstant,
            npcId: checked((int)OnlineAwardProtocol.SpartaNpcId)));
        Check.Equal(
            (int)OnlineAwardExecutionDisposition.AlreadyClaimed,
            (int)already.Disposition,
            "a distinct operation is fenced by character, realm, and day");

        await AssertCommittedStateAsync(dataSource, fixture, receipt);
        await AssertSettlementDeltaGuardAsync(dataSource, fixture);
        return new HistoricalClaim(envelope, receipt);
    }

    private static async Task AssertCommittedStateAsync(
        NpgsqlDataSource dataSource,
        OnlineAwardFixture fixture,
        OnlineAwardExecutionReceipt receipt)
    {
        await using var command = dataSource.CreateCommand(
            """
            SELECT character_row.inventory_revision,
                   character_row.online_award_revision,
                   (SELECT count(*) FROM public.command_inbox
                    WHERE command_family = 'online_award'
                      AND aggregate_key = @aggregateKey),
                   (SELECT max(duplicate_count) FROM public.command_inbox
                    WHERE command_family = 'online_award'
                      AND aggregate_key = @aggregateKey),
                   (SELECT count(*) FROM public.command_audit
                    WHERE command_family = 'online_award'
                      AND aggregate_key = @aggregateKey),
                   (SELECT count(*) FROM public.outbox_events
                    WHERE event_type = 'online-award.claimed'
                      AND aggregate_key = @aggregateKey),
                   (SELECT bool_and(ordering_policy = 'strict')
                    FROM public.outbox_events
                    WHERE event_type = 'online-award.claimed'
                      AND aggregate_key = @aggregateKey),
                   (SELECT count(*) FROM public.character_inventory_ledger
                    WHERE character_id = @characterId
                      AND reason_code = 'online_award_daily_claim'),
                   (SELECT count(*) FROM public.online_award_claim_settlements
                    WHERE character_id = @characterId),
                   (SELECT item_deltas::text
                    FROM public.online_award_claim_settlements
                    WHERE character_id = @characterId),
                   (SELECT balance_revision
                    FROM public.online_award_claim_settlements
                    WHERE character_id = @characterId),
                   (SELECT item_content_revision
                    FROM public.online_award_claim_settlements
                    WHERE character_id = @characterId)
            FROM public.character_base character_row
            WHERE character_row.id = @characterId;
            """);
        command.Parameters.AddWithValue("characterId", fixture.CharacterId);
        command.Parameters.AddWithValue(
            "aggregateKey",
            OnlineAwardPersistenceCodec.AggregateKey(fixture.CharacterId));
        await using var reader = await command.ExecuteReaderAsync();
        Check.True(await reader.ReadAsync(),
            "Online Award committed character remains readable");
        Check.True(
            reader.GetInt64(0) == 1 && reader.GetInt64(1) == 1 &&
            reader.GetInt64(2) == 1 && reader.GetInt32(3) == 1 &&
            reader.GetInt64(4) == 1 && reader.GetInt64(5) == 1 &&
            reader.GetBoolean(6) && reader.GetInt64(7) == 7 &&
            reader.GetInt64(8) == 1 &&
            reader.GetInt64(10) == receipt.BalanceRevision &&
            reader.GetString(11) == receipt.ItemContentRevision,
            "claim atomically writes inbox, audit, ledger, settlement, and strict outbox");
        var storedDeltas = reader.GetString(9);
        Check.True(
            storedDeltas.Contains("\"itemId\": 10150", StringComparison.Ordinal) &&
            storedDeltas.Contains("\"itemQuality\": 14", StringComparison.Ordinal) &&
            storedDeltas.Contains("\"quantity\": 5", StringComparison.Ordinal),
            "settlement preserves exact item delta evidence");
        await reader.CloseAsync();

        await using var items = dataSource.CreateCommand(
            """
            SELECT prop_id, item_quality, bound,
                   count(*)::integer, sum(stack)::integer
            FROM public.character_items
            WHERE user_id = @characterId AND item_location = 1
            GROUP BY prop_id, item_quality, bound
            ORDER BY prop_id, item_quality;
            """);
        items.Parameters.AddWithValue("characterId", fixture.CharacterId);
        var actual = new List<(int, short, short, int, int)>();
        await using var itemReader = await items.ExecuteReaderAsync();
        while (await itemReader.ReadAsync())
        {
            actual.Add((
                itemReader.GetInt32(0),
                itemReader.GetInt16(1),
                itemReader.GetInt16(2),
                itemReader.GetInt32(3),
                itemReader.GetInt32(4)));
        }
        Check.True(actual.SequenceEqual(new[]
        {
            (10134, (short)1, (short)0, 1, 5),
            (10150, (short)10, (short)0, 4, 4),
            (10150, (short)14, (short)0, 1, 1),
            (11005, (short)1, (short)0, 1, 5)
        }), "claim grants one Godly, four Smart, five Dew, and five Feathers");
    }

    private static async Task AssertSettlementDeltaGuardAsync(
        NpgsqlDataSource dataSource,
        OnlineAwardFixture fixture)
    {
        try
        {
            await using var command = dataSource.CreateCommand(
                """
                INSERT INTO public.online_award_claim_settlements (
                    realm_id, account_id, character_id, claim_day,
                    balance_revision, balance_sha256, item_content_revision,
                    item_deltas, inventory_revision, online_award_revision,
                    command_inbox_id, audit_id, event_id)
                SELECT realm_id, account_id, character_id, claim_day + 1,
                       balance_revision, balance_sha256, item_content_revision,
                       '[{"itemId":10150,"itemQuality":14,"bound":0,"quantity":2}]'::jsonb,
                       inventory_revision, online_award_revision,
                       command_inbox_id, audit_id, gen_random_uuid()
                FROM public.online_award_claim_settlements
                WHERE character_id = @characterId;
                """);
            command.Parameters.AddWithValue(
                "characterId",
                fixture.CharacterId);
            await command.ExecuteNonQueryAsync();
            throw new InvalidOperationException(
                "A settlement accepted deltas unlike its balance revision.");
        }
        catch (PostgresException exception) when (
            exception.SqlState == PostgresErrorCodes.CheckViolation &&
            exception.MessageText.Contains(
                "deltas must equal",
                StringComparison.Ordinal))
        {
            // Exact revision-to-delta evidence is DB-enforced.
        }
    }

    private static async Task AssertBagFullRollbackAsync(
        NpgsqlDataSource dataSource,
        PostgresOnlineAwardCommandExecutor executor,
        RealmCalendar calendar)
    {
        var fixture = await CreateOnlineAwardFixtureAsync(
            dataSource,
            "bagfull",
            fillBag: true);
        var result = await executor.ExecuteAsync(CreateEnvelope(
            fixture, calendar, Guid.NewGuid(), ReviewedInstant));
        Check.Equal(
            (int)OnlineAwardExecutionDisposition.BagFull,
            (int)result.Disposition,
            "full bag rejects the complete award");
        await using var command = dataSource.CreateCommand(
            """
            SELECT inventory_revision, online_award_revision,
                   (SELECT count(*) FROM public.online_award_claim_settlements
                    WHERE character_id = @characterId),
                   (SELECT count(*) FROM public.command_inbox
                    WHERE aggregate_key = @aggregateKey)
            FROM public.character_base WHERE id = @characterId;
            """);
        command.Parameters.AddWithValue("characterId", fixture.CharacterId);
        command.Parameters.AddWithValue(
            "aggregateKey",
            OnlineAwardPersistenceCodec.AggregateKey(fixture.CharacterId));
        await using var reader = await command.ExecuteReaderAsync();
        Check.True(
            await reader.ReadAsync() &&
            reader.GetInt64(0) == 0 && reader.GetInt64(1) == 0 &&
            reader.GetInt64(2) == 0 && reader.GetInt64(3) == 0,
            "bag-full failure leaves no durable mutation or receipt");
    }

    private static async Task AssertConcurrentClaimAsync(
        NpgsqlDataSource dataSource,
        PostgresOnlineAwardCommandExecutor executor,
        RealmCalendar calendar)
    {
        var fixture = await CreateOnlineAwardFixtureAsync(
            dataSource,
            "concurrent");
        var tasks = new[]
        {
            executor.ExecuteAsync(CreateEnvelope(
                fixture, calendar, Guid.NewGuid(), ReviewedInstant)),
            executor.ExecuteAsync(CreateEnvelope(
                fixture, calendar, Guid.NewGuid(), ReviewedInstant))
        };
        var dispositions = (await Task.WhenAll(tasks))
            .Select(static result => result.Disposition)
            .OrderBy(static value => value)
            .ToArray();
        Check.True(
            dispositions.SequenceEqual(new[]
            {
                OnlineAwardExecutionDisposition.Committed,
                OnlineAwardExecutionDisposition.AlreadyClaimed
            }.OrderBy(static value => value)),
            "concurrent daily operations yield one commit and one daily fence");
    }

    private static async Task AssertRealmDayBoundaryAsync(
        NpgsqlDataSource dataSource,
        PostgresOnlineAwardCommandExecutor executor,
        RealmCalendar calendar)
    {
        var fixture = await CreateOnlineAwardFixtureAsync(
            dataSource,
            "boundary");
        var before = new DateTimeOffset(
            2026, 8, 21, 15, 59, 59, TimeSpan.Zero);
        var after = before.AddSeconds(1);
        Check.True(
            calendar.GetDay(before).AddDays(1) == calendar.GetDay(after),
            "Manila midnight advances the realm day");
        var first = await executor.ExecuteAsync(CreateEnvelope(
            fixture, calendar, Guid.NewGuid(), before));
        var second = await executor.ExecuteAsync(CreateEnvelope(
            fixture, calendar, Guid.NewGuid(), after));
        Check.True(
            first.Disposition == OnlineAwardExecutionDisposition.Committed &&
            second.Disposition == OnlineAwardExecutionDisposition.Committed &&
            second.Receipt!.OnlineAwardRevision == 2,
            "claims are allowed once on each side of the realm-day boundary");
    }

    private static bool HasSameReceipt(
        OnlineAwardExecutionReceipt? actual,
        OnlineAwardExecutionReceipt expected) =>
        actual is not null &&
        actual.CharacterId == expected.CharacterId &&
        actual.RealmId == expected.RealmId &&
        actual.ClaimDay == expected.ClaimDay &&
        actual.NativeResultSubId == expected.NativeResultSubId &&
        actual.BalanceRevision == expected.BalanceRevision &&
        actual.BalanceSha256 == expected.BalanceSha256 &&
        actual.ItemContentRevision == expected.ItemContentRevision &&
        actual.ItemDeltas.SequenceEqual(expected.ItemDeltas) &&
        actual.InventoryRevision == expected.InventoryRevision &&
        actual.OnlineAwardRevision == expected.OnlineAwardRevision &&
        actual.AuditId == expected.AuditId &&
        actual.EventId == expected.EventId;
}
