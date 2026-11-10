using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Godswar.Server.Infrastructure.Inventory;
using Npgsql;
using NpgsqlTypes;

namespace Godswar.Server.Infrastructure.Trade;

// TRADE (review finding 1/13): the economy evidence every other wallet/bag writer leaves, so the
// report-only reconciliation (character_wallet_reconciliation, character_inventory_reconciliation,
// PostgresReconciliationSnapshot) sees a trade or stall sale as an ordinary ledgered command:
// - PostgresCharacterEconomyBaseline.EnsureAsync for both characters first (as the 26 other
//   writers do); a character without a baseline is refused rather than given revisions that
//   EnsureAsync could never baseline later;
// - per character one command_audit + command_inbox row (family player_trade / player_stall,
//   aggregate character:{id}, the trade id as operation id);
// - wallet_revision / inventory_revision advanced by exactly one, only for the part that changed,
//   guarded on the locked before values;
// - one character_currency_ledger row per changed currency at the new wallet revision;
// - character_inventory_ledger rows at the new inventory revision: giver 'delete' (row before the
//   move), receiver 'add' (row in its final slot), a stall split is seller 'update' + buyer 'add'.
// States are to_jsonb(row) with state_contract_version 1, as the kit-bag move/delete executors write.
internal static partial class PlayerTradeStore
{
    internal const string TradeFamily = "player_trade";
    internal const string StallFamily = "player_stall";
    private const int EvidenceCommandTimeoutSeconds = 30;

    internal sealed record InventoryEntry(long ItemInstanceId, string Kind, string? BeforeState, string? AfterState);

    private static Task<bool> EnsureBaselineAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, int accountId, int characterId,
        CancellationToken cancellationToken) =>
        PostgresCharacterEconomyBaseline.EnsureAsync(
            connection, transaction, accountId, characterId, EvidenceCommandTimeoutSeconds, cancellationToken);

    private static async Task<Dictionary<long, string>> ReadStatesAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, IEnumerable<long> ids,
        CancellationToken cancellationToken)
    {
        var states = new Dictionary<long, string>();
        var array = ids.Distinct().ToArray();
        if (array.Length == 0) return states;
        await using var command = new NpgsqlCommand("""
            SELECT item.id, to_jsonb(item)::text FROM public.character_items item WHERE item.id = ANY(@ids)
            """, connection, transaction);
        command.Parameters.AddWithValue("ids", array);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) states[reader.GetInt64(0)] = reader.GetString(1);
        if (states.Count != array.Length)
            throw new InvalidOperationException("A moved item row could not be read back.");
        return states;
    }

    /// <summary>Ledger entries for <paramref name="self"/>: its offers leave, the partner's arrive.</summary>
    private static List<InventoryEntry> EntriesFor(
        PlayerTradeCommitSide self, PlayerTradeCommitSide partner,
        IReadOnlyDictionary<long, string> before, IReadOnlyDictionary<long, string> after)
    {
        var entries = new List<InventoryEntry>();
        foreach (var item in self.Items) entries.Add(new(item.InstanceId, "delete", before[item.InstanceId], null));
        foreach (var item in partner.Items) entries.Add(new(item.InstanceId, "add", null, after[item.InstanceId]));
        return entries;
    }

    private static async Task WriteCharacterEvidenceAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string family, Guid operationId,
        int accountId, int characterId, int partnerCharacterId, LockedWallet before, int silverAfter, int goldAfter,
        IReadOnlyList<InventoryEntry> entries, CancellationToken cancellationToken)
    {
        var silverDelta = (long)silverAfter - before.Silver;
        var goldDelta = (long)goldAfter - before.Gold;
        var walletChanged = silverDelta != 0 || goldDelta != 0;
        var inventoryChanged = entries.Count > 0;
        if (!walletChanged && !inventoryChanged) return;
        if (entries.Count > 255) throw new InvalidOperationException("Too many inventory ledger entries.");
        var walletRevision = before.WalletRevision + (walletChanged ? 1 : 0);
        var inventoryRevision = before.InventoryRevision + (inventoryChanged ? 1 : 0);

        var request = JsonSerializer.Serialize(new
        {
            operationId,
            characterId,
            partnerCharacterId,
            silverDelta,
            goldDelta,
            items = entries.Select(static e => new { e.ItemInstanceId, e.Kind })
        });
        var result = JsonSerializer.Serialize(new
        {
            operationId,
            characterId,
            partnerCharacterId,
            silverBefore = before.Silver,
            silverAfter,
            goldBefore = before.Gold,
            goldAfter,
            walletRevision,
            inventoryRevision,
            items = entries.Select(static e => new { e.ItemInstanceId, e.Kind })
        });
        var inboxId = await InsertInboxAsync(connection, transaction, family, operationId, accountId, characterId,
            request, result, cancellationToken);

        await using (var update = new NpgsqlCommand("""
            UPDATE public.character_base
            SET "Money" = @silver, "Stone" = @gold,
                wallet_revision = @walletRevision, inventory_revision = @inventoryRevision
            WHERE id = @c AND account_id = @a
              AND "Money" = @silverBefore AND "Stone" = @goldBefore
              AND wallet_revision = @walletBefore AND inventory_revision = @inventoryBefore
            """, connection, transaction))
        {
            update.Parameters.AddWithValue("silver", silverAfter);
            update.Parameters.AddWithValue("gold", goldAfter);
            update.Parameters.AddWithValue("walletRevision", walletRevision);
            update.Parameters.AddWithValue("inventoryRevision", inventoryRevision);
            update.Parameters.AddWithValue("c", characterId);
            update.Parameters.AddWithValue("a", accountId);
            update.Parameters.AddWithValue("silverBefore", before.Silver);
            update.Parameters.AddWithValue("goldBefore", before.Gold);
            update.Parameters.AddWithValue("walletBefore", before.WalletRevision);
            update.Parameters.AddWithValue("inventoryBefore", before.InventoryRevision);
            if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
                throw new InvalidOperationException($"Character {characterId} wallet changed under the trade lock.");
        }

        foreach (var (code, delta, balanceBefore, balanceAfter) in new[]
                 {
                     ("silver", silverDelta, (long)before.Silver, (long)silverAfter),
                     ("gold", goldDelta, (long)before.Gold, (long)goldAfter)
                 })
        {
            if (delta == 0) continue;   // the ledger CHECK forbids a zero delta
            await using var currency = new NpgsqlCommand("""
                INSERT INTO public.character_currency_ledger (
                    command_inbox_id, account_id, character_id, wallet_revision,
                    currency_code, delta, balance_before, balance_after, reason_code)
                VALUES (@inbox, @a, @c, @revision, @code, @delta, @before, @after, @reason)
                """, connection, transaction);
            currency.Parameters.AddWithValue("inbox", inboxId);
            currency.Parameters.AddWithValue("a", accountId);
            currency.Parameters.AddWithValue("c", characterId);
            currency.Parameters.AddWithValue("revision", walletRevision);
            currency.Parameters.AddWithValue("code", code);
            currency.Parameters.AddWithValue("delta", delta);
            currency.Parameters.AddWithValue("before", balanceBefore);
            currency.Parameters.AddWithValue("after", balanceAfter);
            currency.Parameters.AddWithValue("reason", family);
            if (await currency.ExecuteNonQueryAsync(cancellationToken) != 1)
                throw new InvalidOperationException("Trade currency ledger was not inserted once.");
        }

        for (var ordinal = 0; ordinal < entries.Count; ordinal++)
        {
            var entry = entries[ordinal];
            await using var inventory = new NpgsqlCommand("""
                INSERT INTO public.character_inventory_ledger (
                    command_inbox_id, account_id, character_id, inventory_revision, entry_ordinal,
                    item_instance_id, mutation_kind, state_contract_version, before_state, after_state, reason_code)
                VALUES (@inbox, @a, @c, @revision, @ordinal, @item, @kind, 1, @before, @after, @reason)
                """, connection, transaction);
            inventory.Parameters.AddWithValue("inbox", inboxId);
            inventory.Parameters.AddWithValue("a", accountId);
            inventory.Parameters.AddWithValue("c", characterId);
            inventory.Parameters.AddWithValue("revision", inventoryRevision);
            inventory.Parameters.AddWithValue("ordinal", checked((short)ordinal));
            inventory.Parameters.AddWithValue("item", entry.ItemInstanceId);
            inventory.Parameters.AddWithValue("kind", entry.Kind);
            inventory.Parameters.Add("before", NpgsqlDbType.Jsonb).Value = (object?)entry.BeforeState ?? DBNull.Value;
            inventory.Parameters.Add("after", NpgsqlDbType.Jsonb).Value = (object?)entry.AfterState ?? DBNull.Value;
            inventory.Parameters.AddWithValue("reason", family);
            if (await inventory.ExecuteNonQueryAsync(cancellationToken) != 1)
                throw new InvalidOperationException("Trade inventory ledger was not inserted once.");
        }
    }

    private static async Task<long> InsertInboxAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string family, Guid operationId,
        int accountId, int characterId, string requestPayload, string resultPayload, CancellationToken cancellationToken)
    {
        var principalKey = accountId.ToString(CultureInfo.InvariantCulture);
        var aggregateKey = $"character:{characterId}";
        var requestHash = SHA256.HashData(Encoding.UTF8.GetBytes(requestPayload));
        var resultHash = SHA256.HashData(Encoding.UTF8.GetBytes(resultPayload));
        long auditId;
        await using (var audit = new NpgsqlCommand("""
            INSERT INTO public.command_audit (
                principal_type, principal_key, aggregate_type, aggregate_key, command_family,
                operation_id, request_hash, outcome_code, detail_payload)
            VALUES ('account', @principal, 'character', @aggregate, @family, @operation, @requestHash,
                    'committed', @request)
            RETURNING id
            """, connection, transaction))
        {
            audit.Parameters.AddWithValue("principal", principalKey);
            audit.Parameters.AddWithValue("aggregate", aggregateKey);
            audit.Parameters.AddWithValue("family", family);
            audit.Parameters.Add("operation", NpgsqlDbType.Bytea).Value = operationId.ToByteArray();
            audit.Parameters.Add("requestHash", NpgsqlDbType.Bytea).Value = requestHash;
            audit.Parameters.Add("request", NpgsqlDbType.Jsonb).Value = requestPayload;
            auditId = await audit.ExecuteScalarAsync(cancellationToken) is long id && id > 0
                ? id
                : throw new InvalidOperationException("Trade audit returned no identity.");
        }

        await using var inbox = new NpgsqlCommand("""
            INSERT INTO public.command_inbox (
                principal_type, principal_key, aggregate_type, aggregate_key, command_family,
                operation_id, request_hash, result_contract_version, result_code, result_payload,
                result_hash, audit_id)
            VALUES ('account', @principal, 'character', @aggregate, @family, @operation, @requestHash,
                    1, 'committed', @result, @resultHash, @audit)
            RETURNING id
            """, connection, transaction);
        inbox.Parameters.AddWithValue("principal", principalKey);
        inbox.Parameters.AddWithValue("aggregate", aggregateKey);
        inbox.Parameters.AddWithValue("family", family);
        inbox.Parameters.Add("operation", NpgsqlDbType.Bytea).Value = operationId.ToByteArray();
        inbox.Parameters.Add("requestHash", NpgsqlDbType.Bytea).Value = requestHash;
        inbox.Parameters.Add("result", NpgsqlDbType.Jsonb).Value = resultPayload;
        inbox.Parameters.Add("resultHash", NpgsqlDbType.Bytea).Value = resultHash;
        inbox.Parameters.AddWithValue("audit", auditId);
        return await inbox.ExecuteScalarAsync(cancellationToken) is long inboxId && inboxId > 0
            ? inboxId
            : throw new InvalidOperationException("Trade inbox returned no identity.");
    }
}
