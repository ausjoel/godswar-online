using System.Text.Json.Nodes;
using Godswar.Server.Application.FactionCrier;
using Godswar.Server.Infrastructure.FactionCrier;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresFactionCrierCommandIntegrationChecks
{
    private static async Task AssertStoredReceiptTamperRejectedAsync(
        NpgsqlDataSource dataSource,
        CrierFixture fixture)
    {
        await using var command = dataSource.CreateCommand(
            """
            SELECT result_payload::text, result_hash, audit_id
            FROM public.command_inbox
            WHERE principal_key = @principalKey
              AND aggregate_key = @aggregateKey
              AND command_family = 'faction_crier';
            """);
        command.Parameters.AddWithValue(
            "principalKey",
            fixture.AccountId.ToString(
                System.Globalization.CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue(
            "aggregateKey",
            FactionCrierPersistenceCodec.AggregateKey(fixture.CharacterId));
        await using var reader = await command.ExecuteReaderAsync();
        Check.True(
            await reader.ReadAsync(),
            "daily inbox stores one replay receipt");
        var payload = JsonNode.Parse(reader.GetString(0))?.AsObject() ??
            throw new InvalidDataException(
                "The Faction Crier replay fixture has no JSON object.");
        var expectedHash = reader.GetFieldValue<byte[]>(1);
        var auditId = reader.GetInt64(2);
        Check.True(
            !await reader.ReadAsync(),
            "daily inbox identity is unique");
        payload["nativeResultSubId"] =
            payload["nativeResultSubId"]?.GetValue<int>() + 1;
        Check.Throws<InvalidDataException>(
            () => FactionCrierPersistenceCodec.DecodeAndVerify(
                payload.ToJsonString(),
                expectedHash,
                auditId),
            "semantic receipt tampering is rejected by the canonical hash");
    }

    private static async Task AssertDailySettlementAsync(
        NpgsqlDataSource dataSource,
        CrierFixture fixture,
        FactionCrierExecutionReceipt receipt,
        long balanceRevision,
        DateOnly claimDay)
    {
        await using var command = dataSource.CreateCommand(
            """
            SELECT claim_day, plate_item_id, balance_revision,
                   item_content_revision, wallet_revision,
                   inventory_revision, progression_revision,
                   faction_crier_revision,
                   item_content_revision = (
                       SELECT revision
                       FROM public.item_template_content_publication
                       WHERE family = 'items')
            FROM public.faction_crier_daily_claims
            WHERE character_id = @characterId;
            """);
        command.Parameters.AddWithValue("characterId", fixture.CharacterId);
        await using var reader = await command.ExecuteReaderAsync();
        Check.True(
            await reader.ReadAsync() &&
            reader.GetFieldValue<DateOnly>(0) == claimDay &&
            reader.GetInt32(1) == 3820 &&
            reader.GetInt64(2) == balanceRevision &&
            reader.GetString(3).Length == 64 &&
            reader.GetInt64(4) == receipt.WalletRevision &&
            reader.GetInt64(5) == receipt.InventoryRevision &&
            reader.GetInt64(6) == receipt.ProgressionRevision &&
            reader.GetInt64(7) == receipt.FactionCrierRevision &&
            reader.GetBoolean(8) &&
            !await reader.ReadAsync(),
            "daily settlement pins exact period, content, and revisions");
    }

    private static async Task AssertWeeklySettlementAsync(
        NpgsqlDataSource dataSource,
        CrierFixture fixture,
        FactionCrierExecutionReceipt receipt,
        long balanceRevision,
        int goldCost,
        DateOnly weekStart)
    {
        await using var command = dataSource.CreateCommand(
            """
            SELECT week_start, plate_item_id, gold_cost,
                   balance_revision, item_content_revision,
                   wallet_revision, inventory_revision,
                   progression_revision, faction_crier_revision,
                   item_content_revision = (
                       SELECT revision
                       FROM public.item_template_content_publication
                       WHERE family = 'items')
            FROM public.faction_crier_weekly_reclaims
            WHERE character_id = @characterId;
            """);
        command.Parameters.AddWithValue("characterId", fixture.CharacterId);
        await using var reader = await command.ExecuteReaderAsync();
        Check.True(
            await reader.ReadAsync() &&
            reader.GetFieldValue<DateOnly>(0) == weekStart &&
            reader.GetInt32(1) == 3820 &&
            reader.GetInt32(2) == goldCost &&
            reader.GetInt64(3) == balanceRevision &&
            reader.GetString(4).Length == 64 &&
            reader.GetInt64(5) == receipt.WalletRevision &&
            reader.GetInt64(6) == receipt.InventoryRevision &&
            reader.GetInt64(7) == receipt.ProgressionRevision &&
            reader.GetInt64(8) == receipt.FactionCrierRevision &&
            reader.GetBoolean(9) &&
            !await reader.ReadAsync(),
            "weekly settlement pins exact period, cost, content, and revisions");
    }

    private static async Task AssertExchangeSettlementAsync(
        NpgsqlDataSource dataSource,
        CrierFixture fixture,
        FactionCrierExecutionReceipt receipt,
        string operation,
        int subId,
        IReadOnlyList<int> consumedItems,
        int? grantedItemId,
        string currencyCode,
        int currencyCost)
    {
        await using var command = dataSource.CreateCommand(
            """
            SELECT operation, sub_id, consumed_item_ids,
                   granted_item_id, currency_code, currency_cost,
                   awarded_experience, awarded_talent_points,
                   balance_revision = (
                       SELECT revision
                       FROM public.faction_crier_balance_settings
                       WHERE setting_id = 1),
                   item_content_revision = (
                       SELECT revision
                       FROM public.item_template_content_publication
                       WHERE family = 'items'),
                   wallet_revision, inventory_revision,
                   progression_revision, faction_crier_revision
            FROM public.faction_crier_exchange_settlements
            WHERE character_id = @characterId;
            """);
        command.Parameters.AddWithValue("characterId", fixture.CharacterId);
        await using var reader = await command.ExecuteReaderAsync();
        Check.True(await reader.ReadAsync(),
            "exchange settlement row exists");
        var grantedMatches = grantedItemId.HasValue
            ? !reader.IsDBNull(3) && reader.GetInt32(3) == grantedItemId.Value
            : reader.IsDBNull(3);
        Check.True(
            reader.GetString(0) == operation &&
            reader.GetInt16(1) == subId &&
            reader.GetFieldValue<int[]>(2).SequenceEqual(consumedItems) &&
            grantedMatches &&
            reader.GetString(4) == currencyCode &&
            reader.GetInt32(5) == currencyCost &&
            reader.GetInt32(6) == receipt.AwardedExperience &&
            reader.GetInt32(7) == receipt.AwardedTalentPoints &&
            reader.GetBoolean(8) &&
            reader.GetBoolean(9) &&
            reader.GetInt64(10) == receipt.WalletRevision &&
            reader.GetInt64(11) == receipt.InventoryRevision &&
            reader.GetInt64(12) == receipt.ProgressionRevision &&
            reader.GetInt64(13) == receipt.FactionCrierRevision &&
            !await reader.ReadAsync(),
            "exchange settlement pins exact inputs, rewards, and revisions");
    }
}
