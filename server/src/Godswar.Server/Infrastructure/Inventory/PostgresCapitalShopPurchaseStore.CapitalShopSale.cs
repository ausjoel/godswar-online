using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.State;
using Npgsql;
using NpgsqlTypes;

namespace Godswar.Server.Infrastructure.Inventory;

/// <summary>
/// Selling to a capital NPC shop. The client's sell request (opcode 10060)
/// addresses one kit bag stack and carries no quantity, so a sale always moves
/// the whole stack and credits silver in the same transaction that removes it.
/// </summary>
internal sealed partial class PostgresCapitalShopPurchaseStore
{
    /// <summary>
    /// Live observation: a sale pays 20% of the item template's <c>Money</c>
    /// across the whole stack, truncated. The total is computed before the rate,
    /// which is why a stack of five Money-21 potions pays 21 rather than 20.
    /// Five live sales match exactly: Money 21 x 5 = 21, Money 21 x 10 = 42,
    /// Money 39 x 10 = 78, Money 7696 = 1539 and Money 8656 = 1731. The rate
    /// follows the item value and never the shop's asking price, because items
    /// 4001 and 4031 are listed at 25 and 40 yet both pay 21.
    /// </summary>
    private const int SaleValueBasisPoints = 2_000;

    private const string CapitalShopSaleFamily = "capital_shop_sale";

    public async Task<CapitalShopSaleResult> SellCapitalShopItemAsync(
        int accountId,
        int characterId,
        Guid saleId,
        int sourceSlot,
        CancellationToken cancellationToken = default)
    {
        if (accountId <= 0 ||
            characterId <= 0 ||
            saleId == Guid.Empty ||
            sourceSlot is < 0 or >= KitBagProjectionSlots)
        {
            throw new ArgumentOutOfRangeException(nameof(sourceSlot));
        }

        await using var connection =
            await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken);

        var character = await LockCapitalShopCharacterAsync(
            connection,
            transaction,
            accountId,
            characterId,
            cancellationToken);
        if (!character.HasValue)
        {
            await transaction.CommitAsync(cancellationToken);
            return CapitalShopSaleResult.Rejected(
                CapitalShopSaleStatus.CharacterNotFound);
        }

        if (!await PostgresCharacterEconomyBaseline.EnsureAsync(
                connection,
                transaction,
                accountId,
                characterId,
                commandTimeoutSeconds: 30,
                cancellationToken))
        {
            await transaction.RollbackAsync(cancellationToken);
            return CapitalShopSaleResult.Rejected(
                CapitalShopSaleStatus.CharacterNotFound);
        }

        var stack = await LockCapitalShopSaleStackAsync(
            connection,
            transaction,
            characterId,
            checked((short)sourceSlot),
            cancellationToken);
        if (!stack.HasValue)
        {
            await transaction.CommitAsync(cancellationToken);
            return CapitalShopSaleResult.Rejected(
                CapitalShopSaleStatus.EmptySlot);
        }

        var money = await ReadCapitalShopSaleMoneyAsync(
            connection,
            transaction,
            stack.Value.ItemId,
            cancellationToken);
        if (money is null or <= 0)
        {
            await transaction.CommitAsync(cancellationToken);
            return CapitalShopSaleResult.Rejected(
                CapitalShopSaleStatus.UnsupportedItem) with
            {
                ItemId = stack.Value.ItemId,
                Quantity = stack.Value.Stack
            };
        }

        // The rate applies to the whole stack, and the total is truncated after
        // the rate rather than per unit.
        // RATES: NPC sell-price multiplier scales the 20% sale rate
        // (public.server_rates).
        var saleBasisPoints = Godswar.Server.Application.Rates.ServerRates
            .Current.Multiply(
                Godswar.Server.Application.Rates.ServerRateKeys.SellPrice,
                SaleValueBasisPoints,
                100_000);
        var earned = checked((int)(
            (long)money.Value * stack.Value.Stack * saleBasisPoints /
            10_000));
        if (earned <= 0)
        {
            await transaction.CommitAsync(cancellationToken);
            return CapitalShopSaleResult.Rejected(
                CapitalShopSaleStatus.UnsupportedItem) with
            {
                ItemId = stack.Value.ItemId,
                Quantity = stack.Value.Stack
            };
        }

        var unitPrice = money.Value;
        var balanceAfter = checked(character.Value.Silver + earned);
        var walletRevision = checked(character.Value.WalletRevision + 1);
        var inventoryRevision = checked(
            character.Value.InventoryRevision + 1);

        await DeleteCapitalShopSoldStackAsync(
            connection,
            transaction,
            characterId,
            stack.Value,
            cancellationToken);
        var inboxId = await InsertCapitalShopSaleEvidenceAsync(
            connection,
            transaction,
            accountId,
            characterId,
            saleId,
            stack.Value,
            unitPrice,
            earned,
            character.Value,
            balanceAfter,
            walletRevision,
            inventoryRevision,
            cancellationToken);
        await UpdateCapitalShopCharacterAsync(
            connection,
            transaction,
            accountId,
            characterId,
            character.Value,
            "silver",
            balanceAfter,
            walletRevision,
            inventoryRevision,
            cancellationToken);
        await InsertCapitalShopSaleLedgersAsync(
            connection,
            transaction,
            inboxId,
            accountId,
            characterId,
            earned,
            character.Value.Silver,
            balanceAfter,
            walletRevision,
            inventoryRevision,
            stack.Value,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        var refreshed = await GetCharacterByIdAsync(
            characterId,
            cancellationToken) ?? throw new InvalidDataException(
                "Sold shop state could not be reloaded.");
        if (refreshed.Silver != balanceAfter)
        {
            throw new InvalidDataException(
                "Sold shop wallet projection is stale.");
        }

        return new CapitalShopSaleResult(
            CapitalShopSaleStatus.Sold,
            refreshed,
            stack.Value.ItemId,
            stack.Value.Stack,
            unitPrice,
            earned,
            balanceAfter);
    }

    private static async Task<CapitalShopSaleStack?>
        LockCapitalShopSaleStackAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        short slot,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT item.id, item.prop_id, item.stack,
                   to_jsonb(item)::text
            FROM public.character_items item
            WHERE item.user_id = @characterId
              AND item.item_location = 1
              AND item.slot_index = @slot
            FOR UPDATE OF item;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue("slot", slot);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var stack = reader.GetInt16(2);
        if (stack < 1)
        {
            throw new InvalidDataException(
                "A sold kit bag stack has a non-positive size.");
        }

        return new CapitalShopSaleStack(
            reader.GetInt64(0),
            reader.GetInt32(1),
            slot,
            stack,
            reader.GetString(3));
    }

    private static async Task<int?> ReadCapitalShopSaleMoneyAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int itemId,
        CancellationToken cancellationToken)
    {
        // The value lives in the mutable item_templates row, which is where the
        // capital vendor props seeds carry Money. The published projection is
        // queried as well because it is the authority for items that the
        // mutable table no longer describes, so whichever source still carries
        // a positive Money prices the sale.
        await using var command = new NpgsqlCommand(
            """
            SELECT stats::text FROM public.item_templates WHERE id = @itemId
            UNION ALL
            SELECT stats::text
            FROM public.official_item_template_content WHERE id = @itemId;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("itemId", itemId);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var money = ReadCapitalShopSaleMoney(reader.GetString(0));
            if (money is > 0)
            {
                return money;
            }
        }

        // Items the server publishes without a template row, such as skill
        // books, are still sellable because the client ships their value.
        return ItemSaleValueFallback.TryGetMoney(itemId, out var fallback)
            ? fallback
            : null;
    }

    private static int? ReadCapitalShopSaleMoney(string stats)
    {
        using var document = JsonDocument.Parse(stats);
        if (!document.RootElement.TryGetProperty("Money", out var money))
        {
            return null;
        }

        return money.ValueKind switch
        {
            JsonValueKind.Number => money.TryGetInt32(out var number)
                ? number
                : null,
            JsonValueKind.String => int.TryParse(
                money.GetString(),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var parsed)
                ? parsed
                : null,
            _ => null
        };
    }

    private static async Task DeleteCapitalShopSoldStackAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        CapitalShopSaleStack stack,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            DELETE FROM public.character_items
            WHERE id = @itemInstanceId
              AND user_id = @characterId
              AND item_location = 1
              AND slot_index = @slot
              AND stack = @stackBefore;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("itemInstanceId", stack.InstanceId);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue("slot", stack.Slot);
        command.Parameters.AddWithValue("stackBefore", stack.Stack);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidDataException(
                "A sold item stack was not removed exactly once.");
        }
    }

    private static async Task<long> InsertCapitalShopSaleEvidenceAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int accountId,
        int characterId,
        Guid saleId,
        CapitalShopSaleStack stack,
        int unitPrice,
        int earned,
        CapitalShopLockedCharacter before,
        int balanceAfter,
        long walletRevision,
        long inventoryRevision,
        CancellationToken cancellationToken)
    {
        var requestPayload = JsonSerializer.Serialize(new
        {
            saleId,
            slot = stack.Slot,
            itemId = stack.ItemId,
            quantity = stack.Stack,
            unitPrice
        });
        var resultPayload = JsonSerializer.Serialize(new
        {
            saleId,
            characterId,
            slot = stack.Slot,
            itemId = stack.ItemId,
            quantity = stack.Stack,
            unitPrice,
            earned,
            silverBefore = before.Silver,
            silverAfter = balanceAfter,
            walletRevision,
            inventoryRevision
        });
        var requestHash = SHA256.HashData(
            Encoding.UTF8.GetBytes(requestPayload));
        var resultHash = SHA256.HashData(
            Encoding.UTF8.GetBytes(resultPayload));
        var operationId = saleId.ToByteArray();
        var principalKey = accountId.ToString(CultureInfo.InvariantCulture);
        var aggregateKey = $"character:{characterId}";

        long auditId;
        await using (var audit = new NpgsqlCommand(
            """
            INSERT INTO public.command_audit (
                principal_type, principal_key, aggregate_type,
                aggregate_key, command_family, operation_id,
                request_hash, outcome_code, detail_payload)
            VALUES ('account', @principalKey, 'character', @aggregateKey,
                    @family, @operationId,
                    @requestHash, 'committed', @requestPayload)
            RETURNING id;
            """,
            connection,
            transaction))
        {
            audit.Parameters.AddWithValue("principalKey", principalKey);
            audit.Parameters.AddWithValue("aggregateKey", aggregateKey);
            audit.Parameters.AddWithValue("family", CapitalShopSaleFamily);
            audit.Parameters.Add("operationId", NpgsqlDbType.Bytea).Value =
                operationId;
            audit.Parameters.Add("requestHash", NpgsqlDbType.Bytea).Value =
                requestHash;
            audit.Parameters.Add("requestPayload", NpgsqlDbType.Jsonb).Value =
                requestPayload;
            auditId = await audit.ExecuteScalarAsync(cancellationToken)
                is long value && value > 0
                ? value
                : throw new InvalidDataException(
                    "Shop sale audit returned no identity.");
        }

        await using var inbox = new NpgsqlCommand(
            """
            INSERT INTO public.command_inbox (
                principal_type, principal_key, aggregate_type,
                aggregate_key, command_family, operation_id,
                request_hash, result_contract_version, result_code,
                result_payload, result_hash, audit_id)
            VALUES ('account', @principalKey, 'character', @aggregateKey,
                    @family, @operationId,
                    @requestHash, 1, 'committed', @resultPayload,
                    @resultHash, @auditId)
            RETURNING id;
            """,
            connection,
            transaction);
        inbox.Parameters.AddWithValue("principalKey", principalKey);
        inbox.Parameters.AddWithValue("aggregateKey", aggregateKey);
        inbox.Parameters.AddWithValue("family", CapitalShopSaleFamily);
        inbox.Parameters.Add("operationId", NpgsqlDbType.Bytea).Value =
            operationId;
        inbox.Parameters.Add("requestHash", NpgsqlDbType.Bytea).Value =
            requestHash;
        inbox.Parameters.Add("resultPayload", NpgsqlDbType.Jsonb).Value =
            resultPayload;
        inbox.Parameters.Add("resultHash", NpgsqlDbType.Bytea).Value =
            resultHash;
        inbox.Parameters.AddWithValue("auditId", auditId);
        return await inbox.ExecuteScalarAsync(cancellationToken)
            is long inboxId && inboxId > 0
            ? inboxId
            : throw new InvalidDataException(
                "Shop sale inbox returned no identity.");
    }

    private static async Task InsertCapitalShopSaleLedgersAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long inboxId,
        int accountId,
        int characterId,
        int earned,
        int balanceBefore,
        int balanceAfter,
        long walletRevision,
        long inventoryRevision,
        CapitalShopSaleStack stack,
        CancellationToken cancellationToken)
    {
        await using (var currency = new NpgsqlCommand(
            """
            INSERT INTO public.character_currency_ledger (
                command_inbox_id, account_id, character_id,
                wallet_revision, currency_code, delta,
                balance_before, balance_after, reason_code)
            VALUES (@inboxId, @accountId, @characterId,
                    @walletRevision, 'silver', @delta,
                    @balanceBefore, @balanceAfter, @family);
            """,
            connection,
            transaction))
        {
            currency.Parameters.AddWithValue("inboxId", inboxId);
            currency.Parameters.AddWithValue("accountId", accountId);
            currency.Parameters.AddWithValue("characterId", characterId);
            currency.Parameters.AddWithValue("walletRevision", walletRevision);
            currency.Parameters.AddWithValue("delta", (long)earned);
            currency.Parameters.AddWithValue(
                "balanceBefore",
                (long)balanceBefore);
            currency.Parameters.AddWithValue("balanceAfter", (long)balanceAfter);
            currency.Parameters.AddWithValue("family", CapitalShopSaleFamily);
            if (await currency.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                throw new InvalidDataException(
                    "Shop sale currency ledger was not inserted once.");
            }
        }

        await using var inventory = new NpgsqlCommand(
            """
            INSERT INTO public.character_inventory_ledger (
                command_inbox_id, account_id, character_id,
                inventory_revision, entry_ordinal,
                item_instance_id, mutation_kind,
                before_state, after_state, reason_code)
            VALUES (@inboxId, @accountId, @characterId,
                    @inventoryRevision, 0,
                    @itemInstanceId, 'delete', @beforeState,
                    NULL, @family);
            """,
            connection,
            transaction);
        inventory.Parameters.AddWithValue("inboxId", inboxId);
        inventory.Parameters.AddWithValue("accountId", accountId);
        inventory.Parameters.AddWithValue("characterId", characterId);
        inventory.Parameters.AddWithValue(
            "inventoryRevision",
            inventoryRevision);
        inventory.Parameters.AddWithValue(
            "itemInstanceId",
            stack.InstanceId);
        inventory.Parameters.Add("beforeState", NpgsqlDbType.Jsonb).Value =
            stack.BeforeState;
        inventory.Parameters.AddWithValue("family", CapitalShopSaleFamily);
        if (await inventory.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidDataException(
                "Shop sale inventory ledger was not inserted once.");
        }
    }

    private readonly record struct CapitalShopSaleStack(
        long InstanceId,
        int ItemId,
        short Slot,
        short Stack,
        string BeforeState);
}
