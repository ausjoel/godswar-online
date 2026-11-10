using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresFactionCrierCommandIntegrationChecks
{
    private static async Task AssertDailyEvidenceGuardsAsync(
        NpgsqlDataSource dataSource,
        CrierFixture fixture)
    {
        await AssertFreeWeeklyEvidenceAcceptedAsync(dataSource, fixture);
        await AssertEvidenceCheckViolationAsync(
            dataSource,
            fixture,
            """
            INSERT INTO public.faction_crier_daily_claims (
                account_id, character_id, realm_id, claim_day,
                plate_item_id, balance_revision, item_content_revision,
                command_inbox_id, audit_id, outbox_event_id,
                wallet_revision, inventory_revision,
                progression_revision, faction_crier_revision)
            SELECT account_id, character_id, realm_id, claim_day + 6,
                   plate_item_id, balance_revision, item_content_revision,
                   command_inbox_id, audit_id, outbox_event_id,
                   wallet_revision, inventory_revision,
                   progression_revision, faction_crier_revision
            FROM public.faction_crier_daily_claims
            WHERE character_id = @characterId;
            """,
            "ck_faction_crier_daily_plate",
            "Sunday daily evidence is rejected");
        await AssertEvidenceCheckViolationAsync(
            dataSource,
            fixture,
            """
            INSERT INTO public.faction_crier_daily_claims (
                account_id, character_id, realm_id, claim_day,
                plate_item_id, balance_revision, item_content_revision,
                command_inbox_id, audit_id, outbox_event_id,
                wallet_revision, inventory_revision,
                progression_revision, faction_crier_revision)
            SELECT account_id, character_id, realm_id, claim_day,
                   plate_item_id + 1, balance_revision,
                   item_content_revision, command_inbox_id, audit_id,
                   outbox_event_id, wallet_revision, inventory_revision,
                   progression_revision, faction_crier_revision
            FROM public.faction_crier_daily_claims
            WHERE character_id = @characterId;
            """,
            "ck_faction_crier_daily_plate",
            "wrong weekday Nameplate evidence is rejected");
    }

    private static async Task AssertFreeWeeklyEvidenceAcceptedAsync(
        NpgsqlDataSource dataSource,
        CrierFixture fixture)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO public.faction_crier_weekly_reclaims (
                account_id, character_id, realm_id, week_start,
                plate_item_id, gold_cost, balance_revision,
                item_content_revision, command_inbox_id, audit_id,
                outbox_event_id, wallet_revision, inventory_revision,
                progression_revision, faction_crier_revision)
            SELECT account_id, character_id, realm_id, claim_day,
                   plate_item_id, 0, balance_revision,
                   item_content_revision, command_inbox_id, audit_id,
                   outbox_event_id, 0, inventory_revision,
                   progression_revision, faction_crier_revision
            FROM public.faction_crier_daily_claims
            WHERE character_id = @characterId;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue(
            "characterId",
            fixture.CharacterId);
        Check.Equal(
            1,
            await command.ExecuteNonQueryAsync(),
            "free weekly evidence permits wallet revision zero");
        await transaction.RollbackAsync();
    }

    private static Task AssertWeeklyEvidenceGuardAsync(
        NpgsqlDataSource dataSource,
        CrierFixture fixture) =>
        AssertEvidenceCheckViolationAsync(
            dataSource,
            fixture,
            """
            INSERT INTO public.faction_crier_weekly_reclaims (
                account_id, character_id, realm_id, week_start,
                plate_item_id, gold_cost, balance_revision,
                item_content_revision, command_inbox_id, audit_id,
                outbox_event_id, wallet_revision, inventory_revision,
                progression_revision, faction_crier_revision)
            SELECT account_id, character_id, realm_id, week_start + 1,
                   plate_item_id, gold_cost, balance_revision,
                   item_content_revision, command_inbox_id, audit_id,
                   outbox_event_id, wallet_revision, inventory_revision,
                   progression_revision, faction_crier_revision
            FROM public.faction_crier_weekly_reclaims
            WHERE character_id = @characterId;
            """,
            "ck_faction_crier_weekly_period",
            "non-Monday weekly evidence is rejected");

    private static Task AssertRenewalEvidenceGuardAsync(
        NpgsqlDataSource dataSource,
        CrierFixture fixture) =>
        AssertEvidenceCheckViolationAsync(
            dataSource,
            fixture,
            """
            INSERT INTO public.faction_crier_exchange_settlements (
                account_id, character_id, realm_id, operation, sub_id,
                consumed_item_ids, granted_item_id, currency_code,
                currency_cost, awarded_experience,
                awarded_talent_points, balance_revision,
                item_content_revision, command_inbox_id, audit_id,
                outbox_event_id, wallet_revision, inventory_revision,
                progression_revision, faction_crier_revision)
            SELECT account_id, character_id, realm_id, operation, sub_id,
                   consumed_item_ids, 3825, currency_code,
                   currency_cost, awarded_experience,
                   awarded_talent_points, balance_revision,
                   item_content_revision, command_inbox_id, audit_id,
                   outbox_event_id, wallet_revision, inventory_revision,
                   progression_revision, faction_crier_revision
            FROM public.faction_crier_exchange_settlements
            WHERE character_id = @characterId;
            """,
            "ck_faction_crier_exchange_stock_items",
            "renewal evidence rejects a target that contradicts its sub-ID");

    private static Task AssertTurnInEvidenceGuardAsync(
        NpgsqlDataSource dataSource,
        CrierFixture fixture) =>
        AssertEvidenceCheckViolationAsync(
            dataSource,
            fixture,
            """
            INSERT INTO public.faction_crier_exchange_settlements (
                account_id, character_id, realm_id, operation, sub_id,
                consumed_item_ids, granted_item_id, currency_code,
                currency_cost, awarded_experience,
                awarded_talent_points, balance_revision,
                item_content_revision, command_inbox_id, audit_id,
                outbox_event_id, wallet_revision, inventory_revision,
                progression_revision, faction_crier_revision)
            SELECT account_id, character_id, realm_id, operation, sub_id,
                   ARRAY[3820,3820,3820], granted_item_id, currency_code,
                   currency_cost, awarded_experience,
                   awarded_talent_points, balance_revision,
                   item_content_revision, command_inbox_id, audit_id,
                   outbox_event_id, wallet_revision, inventory_revision,
                   progression_revision, faction_crier_revision
            FROM public.faction_crier_exchange_settlements
            WHERE character_id = @characterId;
            """,
            "ck_faction_crier_exchange_stock_items",
            "turn-in evidence rejects duplicate consumed Nameplates");

    private static async Task AssertEvidenceCheckViolationAsync(
        NpgsqlDataSource dataSource,
        CrierFixture fixture,
        string sql,
        string constraintName,
        string description)
    {
        try
        {
            await using var command = dataSource.CreateCommand(sql);
            command.Parameters.AddWithValue(
                "characterId",
                fixture.CharacterId);
            await command.ExecuteNonQueryAsync();
            throw new InvalidOperationException(
                $"Invalid evidence was accepted: {description}.");
        }
        catch (PostgresException exception) when (
            exception.SqlState == PostgresErrorCodes.CheckViolation &&
            exception.ConstraintName == constraintName)
        {
            // Immutable evidence rejects facts that contradict stock semantics.
        }
    }
}
