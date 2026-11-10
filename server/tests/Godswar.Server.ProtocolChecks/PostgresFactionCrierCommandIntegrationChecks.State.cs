using Godswar.Server.Application.FactionCrier;
using Godswar.Server.Infrastructure.FactionCrier;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresFactionCrierCommandIntegrationChecks
{
    private static async Task<CrierDurableState> ReadStateAsync(
        NpgsqlDataSource dataSource,
        CrierFixture fixture)
    {
        await using var command = dataSource.CreateCommand(
            """
            SELECT
                character_row.wallet_revision,
                character_row.inventory_revision,
                character_row.progression_reward_revision,
                character_row.faction_crier_revision,
                character_row."Money",
                character_row."Stone",
                character_row."BindingGold",
                character_row."SkillPoint",
                character_row.fighter_job_exp,
                (SELECT count(*) FROM public.command_inbox
                 WHERE principal_key = @principalKey
                   AND aggregate_key = @aggregateKey
                   AND command_family = 'faction_crier'),
                (SELECT count(*) FROM public.command_audit
                 WHERE principal_key = @principalKey
                   AND aggregate_key = @aggregateKey
                   AND command_family = 'faction_crier'),
                (SELECT count(*) FROM public.outbox_events
                 WHERE aggregate_key = @aggregateKey
                   AND event_type = 'faction_crier.operation_settled'),
                COALESCE((SELECT max(aggregate_version)
                 FROM public.outbox_events
                 WHERE aggregate_key = @aggregateKey
                   AND event_type = 'faction_crier.operation_settled'), 0),
                (SELECT count(*) FROM public.character_inventory_ledger
                 WHERE account_id = @accountId
                   AND character_id = @characterId
                   AND reason_code = 'faction_crier'),
                (SELECT count(*) FROM public.character_currency_ledger
                 WHERE account_id = @accountId
                   AND character_id = @characterId
                   AND reason_code = 'faction_crier'),
                COALESCE((SELECT sum(delta)::bigint
                 FROM public.character_currency_ledger
                 WHERE account_id = @accountId
                   AND character_id = @characterId
                   AND reason_code = 'faction_crier'), 0)::bigint,
                COALESCE((SELECT string_agg(
                    currency_code, ',' ORDER BY currency_code)
                 FROM public.character_currency_ledger
                 WHERE account_id = @accountId
                   AND character_id = @characterId
                   AND reason_code = 'faction_crier'), ''),
                (SELECT count(*) FROM public.faction_crier_daily_claims
                 WHERE character_id = @characterId),
                (SELECT count(*) FROM public.faction_crier_weekly_reclaims
                 WHERE character_id = @characterId),
                (SELECT count(*)
                 FROM public.faction_crier_exchange_settlements
                 WHERE character_id = @characterId),
                COALESCE((SELECT max(duplicate_count)
                 FROM public.command_inbox
                 WHERE principal_key = @principalKey
                   AND aggregate_key = @aggregateKey
                   AND command_family = 'faction_crier'), 0),
                COALESCE((SELECT max(request_conflict_count)
                 FROM public.command_inbox
                 WHERE principal_key = @principalKey
                   AND aggregate_key = @aggregateKey
                   AND command_family = 'faction_crier'), 0),
                COALESCE((SELECT is_reconciled
                 FROM public.character_wallet_reconciliation
                 WHERE character_id = @characterId), false),
                COALESCE((SELECT is_reconciled
                 FROM public.character_inventory_reconciliation
                 WHERE character_id = @characterId), false)
            FROM public.character_base character_row
            WHERE character_row.id = @characterId
              AND character_row.account_id = @accountId;
            """);
        command.Parameters.AddWithValue("accountId", fixture.AccountId);
        command.Parameters.AddWithValue("characterId", fixture.CharacterId);
        command.Parameters.AddWithValue(
            "principalKey",
            fixture.AccountId.ToString(
                System.Globalization.CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue(
            "aggregateKey",
            FactionCrierPersistenceCodec.AggregateKey(fixture.CharacterId));
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            throw new InvalidDataException(
                "The Faction Crier fixture disappeared.");
        }

        return new CrierDurableState(
            reader.GetInt64(0),
            reader.GetInt64(1),
            reader.GetInt64(2),
            reader.GetInt64(3),
            reader.GetInt32(4),
            reader.GetInt32(5),
            reader.GetInt32(6),
            reader.GetInt32(7),
            reader.GetInt64(8),
            reader.GetInt64(9),
            reader.GetInt64(10),
            reader.GetInt64(11),
            reader.GetInt64(12),
            reader.GetInt64(13),
            reader.GetInt64(14),
            reader.GetInt64(15),
            reader.GetString(16),
            reader.GetInt64(17),
            reader.GetInt64(18),
            reader.GetInt64(19),
            reader.GetInt32(20),
            reader.GetInt32(21),
            reader.GetBoolean(22),
            reader.GetBoolean(23));
    }

    private static async Task<NameplateState> ReadNameplateAsync(
        NpgsqlDataSource dataSource,
        CrierFixture fixture,
        int itemId)
    {
        await using var command = dataSource.CreateCommand(
            """
            SELECT count(*)::integer, COALESCE(sum(stack), 0)::integer
            FROM public.character_items
            WHERE user_id = @characterId
              AND item_location = 1
              AND prop_id = @itemId;
            """);
        command.Parameters.AddWithValue("characterId", fixture.CharacterId);
        command.Parameters.AddWithValue("itemId", itemId);
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            throw new InvalidDataException("Nameplate state is missing.");
        }
        return new NameplateState(reader.GetInt32(0), reader.GetInt32(1));
    }

    private static void AssertCommitAuthority(
        CrierDurableState state,
        FactionCrierExecutionReceipt receipt,
        int expectedInventoryLedgerCount,
        string expectedCurrencyCode,
        long expectedCurrencyDelta,
        long dailyCount,
        long weeklyCount,
        long exchangeCount,
        string description)
    {
        var expectedCurrencyRows =
            string.IsNullOrEmpty(expectedCurrencyCode) ? 0 : 1;
        Check.True(
            state.WalletRevision == receipt.WalletRevision &&
            state.InventoryRevision == receipt.InventoryRevision &&
            state.ProgressionRevision == receipt.ProgressionRevision &&
            state.FactionCrierRevision == receipt.FactionCrierRevision &&
            state.InboxCount == 1 &&
            state.AuditCount == 1 &&
            state.OutboxCount == 1 &&
            state.OutboxAggregateVersion == state.FactionCrierRevision &&
            state.InventoryLedgerCount == expectedInventoryLedgerCount &&
            state.CurrencyLedgerCount == expectedCurrencyRows &&
            state.CurrencyDelta == expectedCurrencyDelta &&
            state.CurrencyCodes == expectedCurrencyCode &&
            state.DailyCount == dailyCount &&
            state.WeeklyCount == weeklyCount &&
            state.ExchangeCount == exchangeCount &&
            state.WalletReconciled &&
            state.InventoryReconciled,
            $"{description} has exact revisions, ledgers, settlement, and outbox");
    }

    private sealed record CrierDurableState(
        long WalletRevision,
        long InventoryRevision,
        long ProgressionRevision,
        long FactionCrierRevision,
        int Silver,
        int Gold,
        int BindingGold,
        int TalentPoints,
        long Experience,
        long InboxCount,
        long AuditCount,
        long OutboxCount,
        long OutboxAggregateVersion,
        long InventoryLedgerCount,
        long CurrencyLedgerCount,
        long CurrencyDelta,
        string CurrencyCodes,
        long DailyCount,
        long WeeklyCount,
        long ExchangeCount,
        int DuplicateCount,
        int RequestConflictCount,
        bool WalletReconciled,
        bool InventoryReconciled);

    private readonly record struct NameplateState(int Rows, int Stack);
}
